# 诊断、遥测与统计：logging、counters、Activity/trace、diagnostics hooks 是怎么散开的（第十六篇）

这一篇专门讲 Orleans 的“看得见自己”这部分。

这块如果只看表面，很容易以为 Orleans 有一个统一的诊断系统。其实不是。它把诊断拆成了几层，分别散在运行时、流、目录、membership、生命周期、存储、timer、client 这些子系统里。

先把结论说直白一点：

- `ILogger` 负责常规日志，`[LoggerMessage]` 负责把这些日志做成低开销的源码生成调用。
- `Meter` / `Counter` / `Histogram` 负责运行时指标，`Instruments.Meter` 是总的 meter 入口。
- `ActivitySource` + `ActivityPropagationGrainCallFilter` 负责 OpenTelemetry trace 的进出站传播。
- `DiagnosticListener` / `EventSource` 负责更细的诊断事件和生命周期事件，适合订阅、调试和仿真测试。
- `MeterListener` 不是外部 collector 的替代品，而是 Orleans 自己做统计采样、汇总和快照的办法。
- `SiloRuntimeStatistics`、`DeploymentLoadPublisher`、`ActivationRebalancer` 这类东西，表面像统计，其实已经和调度、放置、负载平衡绑在一起了。

如果只记一句话，就是这个：

> Orleans 的诊断不是一套统一框架，而是“日志、指标、追踪、事件、统计快照”几条线一起拼出来的。

---

## 1. 先把整条图压成一张图

```text
运行时某个动作发生
  -> 先打 LoggerMessage 日志
  -> 再写 DiagnosticListener / EventSource 事件
  -> 需要的话再打 Activity
  -> 同时更新 Meter 指标
  -> 如果是统计采样场景，再由 MeterListener 取样
  -> 最后某些 subsystem 把采样结果合成 runtime statistics
  -> 这些 statistics 又会喂给 placement / rebalance / management
```

这张图里最关键的一点是：

Orleans 没有把“日志”和“统计”分成两个完全独立世界。它经常一边发事件，一边记日志，一边记指标，一边还把数据汇总成 `SiloRuntimeStatistics`。

所以读这部分源码时，别只盯一个命名空间看。你会看漏。

---

## 2. `LoggerMessage` 这层：它是 Orleans 最常见的日志入口

关键文件：

- `src/Orleans.Core/Diagnostics/MessagingTrace.cs`
- `src/Orleans.Core/Messaging/ClientMessageCenter.cs`
- `src/Orleans.Core/Messaging/GatewayManager.cs`
- `src/Orleans.Runtime/MembershipService/MembershipAgent.cs`
- `src/Orleans.Streaming/PersistentStreams/PersistentStreamPullingManager.cs`
- `src/Orleans.Reminders/ReminderService/LocalReminderService.cs`
- `src/Orleans.Runtime/Silo/Silo.cs`

Orleans 的运行时日志，大部分都不是直接写 `logger.LogInformation(...)`，而是用 `partial` + `[LoggerMessage]`。

这样做的好处很直接：

- 参数格式化延后到真的要打日志时才做
- 热路径里少一些字符串分配
- 日志事件和 EventId 绑定得更清楚

比如 `MessagingTrace` 里，消息的创建、入队、出队、丢弃、拒绝，都会走这一套。

`ClientMessageCenter`、`GatewayManager`、`MembershipAgent`、`PersistentStreamPullingManager`、`LocalReminderService` 这些模块也一样，都是 subsystem 自己带一组专用日志。

这就带来一个很现实的结果：

Orleans 的日志是“贴着子系统写的”，不是“统一在一个诊断中心里发号施令”。

这很实用，但也很散。

---

## 3. `Activity` / trace 这层：它是 Orleans 的分布式链路追踪入口

关键文件：

- `src/Orleans.Core.Abstractions/Diagnostics/ActivitySources.cs`
- `src/Orleans.Core.Abstractions/Diagnostics/ActivityTagKeys.cs`
- `src/Orleans.Core.Abstractions/Diagnostics/OpenTelemetryHeaders.cs`
- `src/Orleans.Core/Diagnostics/ActivityPropagationGrainCallFilter.cs`
- `src/Orleans.Core/Core/ClientBuilderExtensions.cs`
- `src/Orleans.Runtime/Hosting/CoreHostingExtensions.cs`

### 3.1 Orleans 提前把 ActivitySource 分了四类

`ActivitySources` 直接定义了四个 source：

- `Microsoft.Orleans.Application`
- `Microsoft.Orleans.Runtime`
- `Microsoft.Orleans.Lifecycle`
- `Microsoft.Orleans.Storage`

这不是装饰。

它的意思是：Orleans 认为不同语义的 span 不该混在一个 source 里。

### 3.2 trace 不是自动全开，要靠 filter 挂进去

`AddActivityPropagation(...)` 会注册两个 grain call filter：

- `ActivityPropagationOutgoingGrainCallFilter`
- `ActivityPropagationIncomingGrainCallFilter`

在 `ClientBuilderExtensions` 和 `CoreHostingExtensions` 里，这个能力是显式加进去的；client 侧还可以通过 `EnableDistributedTracing` 配置开关自动启用。

也就是说，trace 是 Orleans 的一项可选能力，不是默认乱开。

### 3.3 Orleans 用 `RequestContext` 传 `traceparent` / `tracestate`

`OpenTelemetryHeaders` 里直接定义了：

- `traceparent`
- `tracestate`

incoming filter 会从 `RequestContext` 里抽这些值，再 `ExtractTraceIdAndState(...)`。

outgoing filter 则会把当前 `Activity` 注入 `RequestContext`。

这套做法很干净：

- Orleans 不自己发明新的 trace header
- 它直接跟 OpenTelemetry 的标准对齐

### 3.4 span 的 tag 也不是随便打的

`ActivityTagKeys` 里能看到几类 tag：

- `rpc.system`、`rpc.service`、`rpc.method`
- `orleans.grain.id`、`orleans.grain.type`
- `orleans.activation.id`
- `orleans.storage.provider`、`orleans.storage.state.name`、`orleans.storage.state.type`
- `orleans.directory.*`
- `exception.*`

`ActivityPropagationGrainCallFilter` 会给 span 补这些标签。

它还会根据接口类型决定 source：

- 存储相关走 `Storage`
- lifecycle / migration 走 `Lifecycle`
- Orleans runtime 内部调用走 `Runtime`
- 业务 grain 走 `Application`

这就是 Orleans trace 的特点：

它不是“所有 RPC 一个样”，而是按语义分层。

---

## 4. `Meter` / counter / histogram 这层：运行时指标是分模块挂的

关键文件：

- `src/Orleans.Core/Diagnostics/Metrics/Instruments.cs`
- `src/Orleans.Core/Diagnostics/Metrics/InstrumentNames.cs`
- `src/Orleans.Core/Diagnostics/Metrics/MessagingInstruments.cs`
- `src/Orleans.Core/Diagnostics/Metrics/MessagingProcessingInstruments.cs`
- `src/Orleans.Core/Diagnostics/Metrics/DirectoryInstruments.cs`
- `src/Orleans.Core/Diagnostics/Metrics/CatalogInstruments.cs`
- `src/Orleans.Core/Diagnostics/Metrics/StorageInstruments.cs`
- `src/Orleans.Core/Diagnostics/Metrics/ReminderInstruments.cs`
- `src/Orleans.Core/Diagnostics/Metrics/GatewayInstruments.cs`
- `src/Orleans.Core/Diagnostics/Metrics/ClientInstruments.cs`
- `src/Orleans.Core/Diagnostics/Metrics/SchedulerInstruments.cs`
- `src/Orleans.Core/Diagnostics/Metrics/WatchdogInstruments.cs`
- `src/Orleans.Core/Diagnostics/Metrics/ConsistentRingInstruments.cs`

### 4.1 Orleans 先定义了一个总 meter

`Instruments.Meter` 的名字很简单：

- `Microsoft.Orleans`

这相当于 Orleans runtime 的公共 metric 容器。

`OrleansInstruments` 还支持通过 `IMeterFactory` 创建这个 meter，说明它是可以接进宿主的 metrics 体系里的。

### 4.2 指标命名是按子系统切的

`InstrumentNames` 里能看到一串前缀很稳定的名字：

- `orleans-messaging-*`
- `orleans-directory-*`
- `orleans-catalog-*`
- `orleans-storage-*`
- `orleans-reminders-*`
- `orleans-streams-*`
- `orleans-gateway-*`
- `orleans-client-*`
- `orleans-scheduler-*`
- `orleans-watchdog-*`

这说明 Orleans 对 metrics 的态度不是“一个统一的大表”，而是“每个子系统自己认领自己的指标”。

### 4.3 里面有几种典型指标

`MessagingInstruments` 里有：

- sent / received bytes
- failed / dropped / rejected / rerouted / expired messages
- gateway connected clients
- ping 相关计数

`DirectoryInstruments` 里有：

- local / remote lookup counters
- cache validation counters
- partition size / cache size / ring size 观察值
- register / unregister counters

`CatalogInstruments` 里有：

- activation count
- activation working set
- activations created / destroyed
- shutdown / failed-to-activate

`StorageInstruments` 里有：

- read / write / clear latency
- read / write / clear errors

`ReminderInstruments` 里有：

- tardiness histogram
- active reminders gauge
- ticks delivered counter

这类指标看起来多，但逻辑很统一：

有延迟就用 histogram，有计数就用 counter，有当前值就用 gauge。

### 4.4 这些指标很多不是纯导出，而是 Orleans 内部也会拿来采样

`GrainMetricsListener` 和 `SiloRuntimeMetricsListener` 都用了 `MeterListener`。

它们的作用不是导出给外部，而是把运行时指标再揉成 Orleans 自己能用的统计：

- `GrainMetricsListener` 维护每种 grain 类型的活跃数量
- `SiloRuntimeMetricsListener` 维护 connected gateway、received messages、sent messages 的总量

然后这些数据会进入：

- `GrainCountStatistics`
- `SiloRuntimeStatistics`

这一步很关键。

它说明 Orleans 的统计不是只有“遥测系统”这一条出口，框架自己也会把 metric 采样结果拿来做内部决策。

---

## 5. `DiagnosticListener` / `EventSource` 这层：它是 runtime 里更细的诊断钩子

关键文件：

- `src/Orleans.Core/Diagnostics/MessagingEvents.cs`
- `src/Orleans.Core/Diagnostics/MembershipEvents.cs`
- `src/Orleans.Core/Diagnostics/ConnectionEvents.cs`
- `src/Orleans.Core/Diagnostics/ClientLifecycleEvents.cs`
- `src/Orleans.Runtime/Diagnostics/SiloLifecycleEvents.cs`
- `src/Orleans.Runtime/Diagnostics/GrainLifecycleEvents.cs`
- `src/Orleans.Runtime/Diagnostics/DispatcherEvents.cs`
- `src/Orleans.Runtime/Diagnostics/ActivationRebalancerEvents.cs`
- `src/Orleans.Runtime/Diagnostics/DeploymentLoadPublisherEvents.cs`
- `src/Orleans.Runtime/Diagnostics/GrainTimerEvents.cs`
- `src/Orleans.Reminders/Diagnostics/ReminderEvents.cs`
- `src/Orleans.Streaming/Diagnostics/StreamingEvents.cs`

### 5.1 `DiagnosticListener` 的特点：可以订阅，事件本身是强类型 payload

这些事件类大多都有一个共同点：

- `ListenerName`
- `AllEvents`
- `EmitXXX(...)`

它们不是单纯写日志，而是把事件作为对象发出去。

比如：

- `MessagingEvents`
- `MembershipEvents`
- `ConnectionEvents`
- `SiloLifecycleEvents`
- `GrainLifecycleEvents`
- `ClientLifecycleEvents`
- `ReminderEvents`
- `StreamingEvents`
- `ActivationRebalancerEvents`
- `DeploymentLoadPublisherEvents`
- `GrainTimerEvents`

这类东西更适合：

- 诊断面板
- 仿真测试
- 本地调试
- 订阅式的工具链

### 5.2 `EventSource` 负责更轻量的系统级事件

`EventSourceEvents` 这一层会把一些热点路径做成 ETW / EventPipe 风格的事件：

- `OrleansCallBackDataEvent`
- `OrleansOutsideRuntimeClientEvent`
- `OrleansInsideRuntimeClientEvent`
- `OrleansDispatcherEvent`
- `OrleansIncomingMessageAgentEvent`

这类事件通常比完整诊断对象更轻，适合性能更敏感的场景。

### 5.3 `MessagingTrace` 是这条线上的总线

`MessagingTrace` 很像 Orleans messaging 侧的诊断中枢。

它会同时做几件事：

- 发 `MessagingEvents`
- 发 `EventSource`
- 更新 metrics
- 再补上 `ILogger` 日志

所以它是典型的“一处触发，多路输出”。

这也是 Orleans 诊断层最像样的地方之一：

同一条消息的生命周期，可以同时被日志、事件、指标和 trace 看见。

---

## 6. 统计采样：Orleans 不是只看“当前值”，它还会自己攒快照

关键文件：

- `src/Orleans.Core/Statistics/GrainMetricsListener.cs`
- `src/Orleans.Core/Statistics/SiloRuntimeMetricsListener.cs`
- `src/Orleans.Core/Statistics/GrainCountStatistics.cs`
- `src/Orleans.Core/Statistics/SiloRuntimeStatistics.cs`
- `src/Orleans.Runtime/Placement/DeploymentLoadPublisher.cs`
- `src/Orleans.Runtime/Placement/Rebalancing/ActivationRebalancerWorker.cs`

### 6.1 `GrainMetricsListener` 把 grain counts 留在进程内

它专门盯 `orleans-grains` 这个 meter。

当 grain 数量加减时，它把每种 grain type 的数量攒到一个 `ConcurrentDictionary<string, int>` 里。

这不是外部监控系统做的事，而是 Orleans 自己在做内存里的统计缓存。

### 6.2 `SiloRuntimeMetricsListener` 盯的是运行时总量

它会跟踪：

- connected client count
- message received total
- message sent total

这些值随后会进入 `SiloRuntimeStatistics`。

`SiloRuntimeStatistics` 不是一个纯数据 DTO，它是一个“运行时快照”：

- activation count
- recently used activation count
- client count
- received / sent messages
- environment statistics
- overload 状态

`DeploymentLoadPublisher` 会把这些统计发到集群里，再让 placement / rebalancer / management 去消费。

### 6.3 `DeploymentLoadPublisherEvents` 和 `ActivationRebalancerEvents` 是统计链上的诊断出口

这两个 listener 很说明问题：

- `DeploymentLoadPublisherEvents` 记录统计发布、收到、刷新、移除
- `ActivationRebalancerEvents` 记录 rebalancing session / cycle start stop

也就是说，统计不是只做数值，还会把“这份数值是怎么来的”暴露成事件。

---

## 7. Orleans 的诊断到底散在哪些模块里

如果只看运行时目录，会发现它不是一坨，而是散在这些地方：

- `Diagnostics/`：`MessagingEvents`、`ConnectionEvents`、`ActivitySources`、`ActivityTagKeys`
- `Diagnostics/Metrics/`：各种 subsystem 的指标定义
- `Statistics/`：统计 listener 和 runtime statistics
- `Messaging/`：`MessagingTrace`、`MessageFactory`、`ClientMessageCenter`
- `Networking/`：连接建立/终止、socket 开关、gateway 收发
- `MembershipService/`：membership 变化事件
- `Lifecycle/`：silo/client/grain lifecycle 事件
- `Catalog/`：activation 统计、回收、working set
- `Placement/`：deployment load publisher、rebalancer、ring 统计
- `Timers/` 和 `Reminders/`：timer/reminder 的诊断事件
- `Streaming/`：streaming events、queue adapter monitors、cache monitors
- `Storage/`：storage latency / error metrics

这就是 Orleans 的老问题：

它的诊断能力很全，但不集中。

你要追一条链，得跨好几个目录。

---

## 8. 这块里我觉得不够干净的地方

### 8.1 `logging`、`EventSource`、`DiagnosticListener`、`Meter` 都在用，但边界不统一

同一件事，有时先打日志，有时先发 event，有时先记 counter，有时四样都来。

这让工具很难只靠一个入口看全。

### 8.2 统计和遥测混在一起了

严格说：

- `Meter` 是遥测
- `MeterListener` 采样出来的 `SiloRuntimeStatistics` 是运行时统计

但 Orleans 常把这两层串着用。

这并不是错，只是边界不清。

### 8.3 诊断入口太分散

如果你想订阅 Orleans 的全部诊断，需要同时盯：

- `ILogger`
- `ActivitySource`
- `DiagnosticListener`
- `EventSource`
- `Meter`

这对框架内部是灵活，对使用者就有点累。

### 8.4 有些指标名和用途并不总是完全对齐

像 `DirectoryInstruments` 里就有注释说某些计数“not used”。

这类东西通常说明两件事：

- 曾经有路径，后来没用了
- 指标命名和实现演进没有完全收口

这在长期演化的框架里很常见，但确实不够利落。

---

## 9. 如果你要自己复刻，我会怎么拆

### 9.1 先把日志和诊断事件分开

日志只做文本和结构化参数。

诊断事件只做可订阅 payload。

别把两者混写在一个 helper 里。

### 9.2 `Meter` 和内部统计快照分层

外部导出用 `Meter`。

内部决策用 `StatisticsSnapshot` 或 `SamplerCache`。

中间通过一个明确的采样器连接，不要靠隐式 static listener。

### 9.3 `Activity` 传播做成显式 opt-in

Orleans 这点其实已经做得不错了。

复刻时也应该保留：

- 默认不开
- 开了才注入 `traceparent`
- 同时支持 OpenTelemetry tag 约定

### 9.4 每个子系统保留自己的诊断面，但统一命名风格

像：

- `messaging`
- `directory`
- `catalog`
- `storage`
- `reminders`
- `streaming`

这些维度可以保留。

但入口应该统一，不要让人去记太多不同的 listener 风格。

---

## 10. 推荐阅读顺序

建议按这个顺序看：

1. `src/Orleans.Core/Diagnostics/MessagingTrace.cs`
2. `src/Orleans.Core/Diagnostics/ActivityPropagationGrainCallFilter.cs`
3. `src/Orleans.Core.Abstractions/Diagnostics/ActivitySources.cs`
4. `src/Orleans.Core.Abstractions/Diagnostics/ActivityTagKeys.cs`
5. `src/Orleans.Core/Diagnostics/Metrics/Instruments.cs`
6. `src/Orleans.Core/Diagnostics/Metrics/InstrumentNames.cs`
7. `src/Orleans.Core/Diagnostics/Metrics/MessagingInstruments.cs`
8. `src/Orleans.Core/Diagnostics/Metrics/MessagingProcessingInstruments.cs`
9. `src/Orleans.Core/Statistics/GrainMetricsListener.cs`
10. `src/Orleans.Core/Statistics/SiloRuntimeMetricsListener.cs`
11. `src/Orleans.Core/Statistics/SiloRuntimeStatistics.cs`
12. `src/Orleans.Runtime/Diagnostics/SiloLifecycleEvents.cs`
13. `src/Orleans.Runtime/Diagnostics/GrainLifecycleEvents.cs`
14. `src/Orleans.Runtime/Diagnostics/DispatcherEvents.cs`
15. `src/Orleans.Reminders/Diagnostics/ReminderEvents.cs`
16. `src/Orleans.Streaming/Diagnostics/StreamingEvents.cs`
17. `src/Orleans.Runtime/Placement/DeploymentLoadPublisher.cs`
18. `src/Orleans.Runtime/Placement/Rebalancing/ActivationRebalancerWorker.cs`

---

## 11. 这一篇的落点

把这一篇压成一句话，就是：

Orleans 的诊断不是一条中心化管道，而是几层能力叠在一起：

- `LoggerMessage` 管常规日志
- `ActivitySource` 管分布式追踪
- `Meter` 管指标
- `DiagnosticListener` / `EventSource` 管运行时事件
- `MeterListener` 管内部统计采样

它们彼此不是替代关系，而是叠加关系。

这套东西的好处是灵活，缺点是散。

如果你后面要继续往下看，我建议下一篇接：

- `Runtime 里的状态、统计和控制面：SiloRuntimeStatistics、DeploymentLoadPublisher、ManagementGrain 是怎么把这些诊断数据吃进去的`

