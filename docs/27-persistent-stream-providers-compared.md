# 持久流 Provider 横向对比：共同骨架和各家差异（第二十七篇）

这一篇专门看 Orleans 的持久流 provider。先把结论说在前面：

- Orleans 的持久流 provider，表面上是好几个不同后端，底层其实共用一套骨架：`IQueueAdapterFactory`、`IQueueAdapter`、`IQueueAdapterReceiver`、`IQueueCache`，再配上 `PersistentStreamProvider`、`PersistentStreamPullingManager`、`PersistentStreamPullingAgent`、`IStreamQueueBalancer` 这些共享运行时。
- 真正拉开差异的，不是“有没有 stream”这件事，而是四个点：`queue adapter factory` 怎么接后端、`cache` 是简单缓冲还是带压力控制、`checkpointer` 有没有、`MessagesDeliveredAsync` 是删消息还是只做确认。
- 从源码看，`EventHub` 是最完整、最重的一支：有独立 cache、checkpoint、pressure monitor、flow controller，支持 rewind。
- `Azure Queue`、`SQS`、`NATS` 这几支更像“取消息 - 投递 - 确认 - 删除”的简单模型，没有 EventHub 那套 checkpoint/cache 组合拳。
- `Memory` provider 则是另一条路：它不碰外部队列，queue 本身就在 grain 里，适合测试、演示和轻量场景。
- 这份仓库里没有看到 Kafka provider 的源码，所以这篇不编 Kafka，不臆测 Kafka。

如果只记一句话，那就是：

> Orleans 的持久流不是“每个 provider 都重写一套流系统”，而是“先用统一 runtime 把拉流、派发、回压、订阅这些事框住，再让不同后端只实现自己的队列接入层”。

---

## 1. 横向对比总表

| Provider | Factory 形态 | Cache | Checkpointer | `IsRewindable` | `MessagesDeliveredAsync` 语义 | 主要失败语义 | 适用场景 |
|---|---|---|---|---|---|---|---|
| `EventHub` | `EventHubAdapterFactory` 同时实现 `IQueueAdapterFactory` / `IQueueAdapter` / `IQueueAdapterCache` | 自定义 `EventHubQueueCache`，带 pressure monitor 和 eviction | 有，`EventHubCheckpointer`，把 partition offset 写到 Azure Table | `true` | 基本空实现，靠 offset + cache 继续推进 | 读取失败重试；checkpoint 是 best-effort | 高吞吐、需要回放、想保留消费位置 |
| `Azure Queue` | `AzureQueueAdapterFactory` | `SimpleQueueAdapterCache` | 没有单独 checkpoint | `false` | 删除已成功交付的消息 | 删除失败只记 warning，消息会再出现 | 简单队列式持久流，逻辑直接 |
| `SQS` | `SQSAdapterFactory` | `SimpleQueueAdapterCache` | 没有单独 checkpoint | `false` | 删除已成功交付的消息 | 删除失败只记 warning，消息会再出现 | AWS SQS 后端，偏简单投递模型 |
| `NATS` | `NatsAdapterFactory` | `SimpleQueueAdapterCache` | 没有单独 checkpoint | `false` | 通过 `ReplyTo` 发 `+ACK` | ack 失败就回到 JetStream 语义里处理 | JetStream 场景，消息流更偏 broker 模型 |
| `Memory` | `MemoryAdapterFactory<TSerializer>` 同时实现工厂、适配器和 cache | `MemoryPooledCache<TSerializer>`，进程内池化缓存 | 没有外部 checkpoint | `true` | 空实现 | 失败主要是内存队列满、序列化失败、进程内异常 | 测试、开发、轻量内存流 |

这里的“主要失败语义”我只按源码里能看到的行为写：没有把后端文档知识掺进来。

---

## 2. 共同骨架：这些 provider 其实都在走同一套框架

关键共享文件：

- `src/Orleans.Streaming/QueueAdapters/IQueueAdapterFactory.cs`
- `src/Orleans.Streaming/QueueAdapters/IQueueAdapter.cs`
- `src/Orleans.Streaming/QueueAdapters/IQueueAdapterReceiver.cs`
- `src/Orleans.Streaming/QueueAdapters/IQueueCache.cs`
- `src/Orleans.Streaming/PersistentStreams/PersistentStreamProvider.cs`
- `src/Orleans.Streaming/PersistentStreams/PersistentStreamPullingManager.cs`
- `src/Orleans.Streaming/PersistentStreams/PersistentStreamPullingAgent.cs`
- `src/Orleans.Streaming/PersistentStreams/IStreamQueueBalancer.cs`
- `src/Orleans.Streaming/PersistentStreams/IStreamQueueCheckpointer.cs`

### 2.1 `Factory -> Adapter -> Receiver -> Cache` 是主骨架

`IQueueAdapterFactory` 负责四件事：

- `CreateAdapter()`
- `GetQueueAdapterCache()`
- `GetStreamQueueMapper()`
- `GetDeliveryFailureHandler()`

`IQueueAdapter` 负责：

- `QueueMessageBatchAsync(...)`
- `CreateReceiver(...)`
- `IsRewindable`
- `Direction`

`IQueueAdapterReceiver` 负责：

- `Initialize(...)`
- `GetQueueMessagesAsync(...)`
- `MessagesDeliveredAsync(...)`
- `Shutdown(...)`

`IQueueCache` 负责：

- `AddToCache(...)`
- `TryPurgeFromCache(...)`
- `GetCacheCursor(...)`
- `IsUnderPressure()`

也就是说，provider 真正要提供的东西并不多。它主要是把“底层怎么写入”“底层怎么读”“读出来的消息怎么缓存”“交付确认之后怎么处理”这四步接上。

### 2.2 `PersistentStreamProvider` 只是总装配，不是具体后端

`PersistentStreamProvider` 自己不认识任何具体后端，它只认：

- `IQueueAdapterFactory`
- `IQueueAdapter`
- `IPersistentStreamPullingManager`
- `IStreamSubscriptionManager`
- `StreamPubSubOptions`
- `StreamLifecycleOptions`

它的工作是把这些东西串起来，然后在生命周期阶段里拉起 pulling agent。

### 2.3 `IStreamQueueBalancer` 是共享的，不是某个 provider 专属

从源码看，queue balancer 不在各 provider 项目里单独实现，而是在共享的 `Orleans.Streaming` 里提供。

`SiloStreamProviderRuntime.InitializePullingAgents(...)` 会去拿：

- keyed `IStreamQueueBalancer`
- 如果没有 keyed，再退回全局 `IStreamQueueBalancer`

这说明 balancer 是“持久流运行时的一层策略”，不是 provider 的私有实现。

默认会有 `ConsistentRingQueueBalancer`，另外源码里还提供了 `DeploymentBasedQueueBalancer` 和 `LeaseBasedQueueBalancer`。它们都在 shared streaming 这一层，不属于某个具体 provider。

---

## 3. 先看最核心的差异：谁有 checkpoint，谁没有

### 3.1 `EventHub` 有 checkpoint，而且是这组 provider 里最重的

`EventHubCheckpointer` 会把 partition offset 存进 Azure Table。初始化时先 `Load()`，拉流时再按条件 `Update()`。

这里的重点是两句：

- `CheckpointExists` 不是“有没有存过任意值”，而是看 offset 是否还停在起始位置
- `Update(...)` 是 best-effort，不保证每次都写

这说明 EventHub 的 checkpoint 设计更像“尽力记住消费位置”，不是“每条消息都强一致落盘”。

### 3.2 其他 provider 没有独立 checkpoint

`Azure Queue`、`SQS`、`NATS`、`Memory` 这几支，源码里都没有像 EventHub 那样单独抽一个 checkpoint 对象。

它们的消费位置要么：

- 靠底层队列的删除/ack 语义维持
- 要么压根就是内存队列，不需要外部 checkpoint

这会直接影响它们能不能像 EventHub 一样做回放。

---

## 4. 再看 cache：简单缓存和“带压力控制的缓存”不是一回事

### 4.1 `Azure Queue`、`SQS`、`NATS` 更像简单缓冲

这几支 provider 都返回 `SimpleQueueAdapterCache`，或者等价的轻量缓存实现。

这种 cache 的作用主要是：

- 让 pulling agent 有一个地方能按 token 取消息
- 给读写分离留一点缓冲

它不是重点，重点还是后端消息队列本身。

### 4.2 `EventHub` 的 cache 是主角之一

`EventHubQueueCacheFactory` 会造出：

- `EventHubQueueCache`
- `ChronologicalEvictionStrategy`
- `FixedSizeBuffer` 对象池
- cache pressure monitor
- block pool monitor

这里已经不是“缓存一下消息”这么简单了，而是在做：

- 消息块管理
- 时间驱逐
- 压力监控
- 读写流控

这也是为什么 EventHub 的实现比其他 provider 重很多。

### 4.3 `Memory` 的 cache 是进程内池化缓存

`MemoryPooledCache<TSerializer>` 也是独立 cache，但它跟 EventHub 的目标不一样。

它的核心点是：

- 用 `PooledQueueCache`
- 用 `ChronologicalEvictionStrategy`
- 通过 `FixedSizeBuffer` 做块池
- `IsUnderPressure()` 直接返回 `false`

也就是说，Memory cache 关注的是进程内对象复用和缓存淘汰，不是外部后端压力。

---

## 5. 各 provider 的差异

### 5.1 `EventHub`: 最完整的一套持久流后端

关键文件：

- `src/Azure/Orleans.Streaming.EventHubs/Providers/Streams/EventHub/EventHubAdapterFactory.cs`
- `src/Azure/Orleans.Streaming.EventHubs/Providers/Streams/EventHub/EventHubAdapterReceiver.cs`
- `src/Azure/Orleans.Streaming.EventHubs/Providers/Streams/EventHub/EventHubCheckpointer.cs`
- `src/Azure/Orleans.Streaming.EventHubs/Providers/Streams/EventHub/EventHubQueueCacheFactory.cs`

它的特点很明确：

- `IsRewindable => true`
- factory 同时实现 factory / adapter / cache
- receiver 初始化时先 load checkpoint，再建 cache，再建 flow controller，再建 receiver
- `MessagesDeliveredAsync(...)` 基本空实现
- `TryPurgeFromCache(...)` 会根据压力决定是否让 cache purge

这里最重要的不是“它能读 Event Hub”，而是它把“回放、缓存、checkpoint、流控”都放到了同一条链里。

我觉得这类实现的优点是能力完整，缺点也很明显：职责太多都堆在一起了，`EventHubAdapterFactory` 很像一个总控制器。

### 5.2 `Azure Queue`: 典型的删消息式持久流

关键文件：

- `src/Azure/Orleans.Streaming.AzureStorage/Providers/Streams/AzureQueue/AzureQueueAdapterFactory.cs`
- `src/Azure/Orleans.Streaming.AzureStorage/Providers/Streams/AzureQueue/AzureQueueAdapter.cs`
- `src/Azure/Orleans.Streaming.AzureStorage/Providers/Streams/AzureQueue/AzureQueueAdapterReceiver.cs`

它的特点也很直白：

- `IsRewindable => false`
- 写入时不支持非空 `StreamSequenceToken`
- receiver 取消息后会把成功交付的消息删掉
- `MessagesDeliveredAsync(...)` 里删消息失败只记 warning

Azure Queue 这条线的语义很朴素：读出来，发下去，确认后删除。它不做回放，也不把 checkpoint 抽成独立概念。

### 5.3 `SQS`: 也是删消息式，但后端换成 AWS 存储

关键文件：

- `src/AWS/Orleans.Streaming.SQS/Streams/SQSAdapterFactory.cs`
- `src/AWS/Orleans.Streaming.SQS/Streams/SQSAdapter.cs`
- `src/AWS/Orleans.Streaming.SQS/Streams/SQSAdapterReceiver.cs`

它和 Azure Queue 的风格很像：

- `IsRewindable => false`
- 不接受非空 `StreamSequenceToken`
- 成功交付后删除消息
- 删除失败只记 warning

差别主要在后端存储实现，不在持久流骨架本身。

### 5.4 `NATS`: 更像 broker / JetStream 风格

关键文件：

- `src/Orleans.Streaming.NATS/Providers/NatsAdapterFactory.cs`
- `src/Orleans.Streaming.NATS/Providers/NatsAdapter.cs`
- `src/Orleans.Streaming.NATS/Providers/NatsQueueAdapterReceiver.cs`
- `src/Orleans.Streaming.NATS/Providers/NatsConnectionManager.cs`

这条线的特征是：

- 先连 NATS，再检查 JetStream 是否可用
- 创建 JetStream stream 时用 workqueue retention
- 写入时把 `NatsBatchContainer` 序列化成字节
- 读取时把 payload 反序列化回来，再补 `EventSequenceTokenV2`
- 交付确认通过 `ReplyTo` 发 `+ACK`

它没有 EventHub 那种独立 checkpoint，也没有显式的 rewind 缓存语义。

所以 NATS 这条实现的核心，不是“缓存式持久流”，而是“JetStream 上的队列式消费 + ack”。

### 5.5 `Memory`: 进程内 grain 队列，适合测试和轻量场景

关键文件：

- `src/Orleans.Streaming/MemoryStreams/MemoryStreamProviderBuilder.cs`
- `src/Orleans.Streaming/MemoryStreams/MemoryAdapterFactory.cs`
- `src/Orleans.Streaming/MemoryStreams/MemoryPooledCache.cs`
- `src/Orleans.Streaming/MemoryStreams/MemoryStreamQueueGrain.cs`

它最特别的地方是：

- `MemoryAdapterFactory<TSerializer>` 同时是 factory、adapter、cache
- 消息存在 `MemoryStreamQueueGrain` 里
- `MemoryStreamQueueGrain` 还是一个可迁移 grain
- 队列有上限，到了 `16384` 就直接抛异常
- `IsRewindable => true`

Memory provider 的本质，不是外部队列适配器，而是“用 grain 模拟了一条队列”。这让它特别适合测试和开发，也让它跟外部后端 provider 有本质区别。

---

## 6. 失败语义到底差在哪

### 6.1 删除失败和 checkpoint 失败，性质完全不一样

在 `Azure Queue` 和 `SQS` 里，`MessagesDeliveredAsync(...)` 的核心动作是删除消息。删失败通常只是 warning。

这意味着：

- 消息可能后面还会再见到
- provider 不是靠 checkpoint 保证语义
- 交付确认更像“尽力删掉”

### 6.2 `EventHub` 的失败更偏“流控 + checkpoint”

EventHub 这里不是删消息，而是：

- 读到消息后放进 cache
- 用 checkpointer 记位置
- 通过 flow controller 控制继续读多少

失败时，重点不是“删没删掉”，而是：

- 读失败
- cache 压力过大
- checkpoint 写不出去

### 6.3 `NATS` 的失败更偏 ack 语义

NATS 的交付确认是对 `ReplyTo` 发 `+ACK`。如果 ack 没发出去，就更接近 broker 的投递语义，而不是本地删除语义。

### 6.4 `Memory` 的失败最直接

Memory 这条线没有外部后端，所以失败常常就是：

- 队列满了
- 序列化失败
- grain / 进程内异常

它没有“回头再删一次”这种外部补偿空间。

---

## 7. 架构上不够干净的点

这部分我只说源码里看得出来的地方，不做过度发挥。

### 7.1 `EventHubAdapterFactory` 责任太重

它同时管：

- 工厂
- adapter
- cache
- receiver 创建
- checkpoint 接入
- failure handler
- queue mapper

这已经不是“factory”了，更像一个大总控。

### 7.2 `MemoryAdapterFactory<TSerializer>` 也是三合一

它同时是：

- `IQueueAdapterFactory`
- `IQueueAdapter`
- `IQueueAdapterCache`

这个设计很省事，但也把职责绑得太紧。后面如果要复刻一个更干净的版本，我会把 adapter、cache、factory 拆开。

### 7.3 队列分发和后端接入被揉得有点紧

从 shared runtime 看，balancer 是通用层；但从具体 provider 看，很多地方还是直接依赖 `HashRingBasedStreamQueueMapper`。

结果就是：

- 后端接入层要关心分区映射
- runtime 又要关心 queue 分布

这两层概念靠得太近了，理解成本会偏高。

### 7.4 `MessagesDeliveredAsync` 的语义不统一

不同 provider 的 `MessagesDeliveredAsync` 差异很大：

- 有的删消息
- 有的 ack
- 有的空实现

接口名字一样，但实际含义差很多。这个是典型的“统一接口下面装了几种完全不同的投递确认模型”。

---

## 8. 推荐阅读顺序

如果你要接着把这块源码吃透，我建议按这个顺序看：

1. 先看 `src/Orleans.Streaming/QueueAdapters/IQueueAdapterFactory.cs`、`IQueueAdapter.cs`、`IQueueAdapterReceiver.cs`、`IQueueCache.cs`，把共享骨架记住。
2. 再看 `src/Orleans.Streaming/PersistentStreams/PersistentStreamProvider.cs` 和 `PersistentStreamPullingManager.cs`，把运行时怎么挂起来弄明白。
3. 然后先看 `EventHub`，因为它最完整，能把 cache、checkpoint、pressure、rewind 一次看全。
4. 接着看 `Azure Queue` 和 `SQS`，你会很容易看出“删消息式 provider”到底长什么样。
5. 再看 `NATS`，补一条 JetStream / ack 风格的实现。
6. 最后看 `Memory`，你会明白 Orleans 是怎么用 grain 自己模拟一条流队列的。

如果下一篇继续接，我建议写这两个方向之一：

- `PersistentStreamPullingManager` 和 `PersistentStreamPullingAgent` 的读拉派发细节
- `EventHubQueueCache` 内部到底怎么做 cache、eviction 和 pressure control

