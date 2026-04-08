# Timer 与 Reminder：两条看起来像、其实完全不同的时间链路（第九篇）

这一篇只讲一件事：Orleans 里“定时”这件事，到底分成了哪两条线。

先把结论说死：

- `RegisterTimer` / `RegisterGrainTimer` 走的是本地 timer，绑定在某个 activation 上。
- Reminder 走的是持久提醒服务，绑定的是 grain 身份，不是某次 activation。
- 本地 timer 不持久，进程没了、activation 退了、timer 也就没了。
- Reminder 会写进 reminder table，服务重启后还能读回来继续跑。
- 但 Reminder 也不是“准点必达、一次不漏”的魔法，它更像“尽量送到一个活的 activation，然后按当前时间重新对齐继续跑”。

如果你只记住一句话，那就记这一句：

> timer 是激活态里的短命回调，reminder 是跨激活、跨重启的持久调度。

---

## 1. 先把整条图压成一张图

```text
本地 timer
  Grain / SystemTarget
    -> RegisterTimer / RegisterGrainTimer
    -> ITimerRegistry
    -> TimerRegistry
    -> GrainTimer / InterleavingGrainTimer
    -> System.Threading.Timer
    -> 给自己发一个本地 one-way message
    -> activation 上下文里执行 callback
    -> 结束后再安排下一次 tick

持久 reminder
  Grain 调用 RegisterOrUpdateReminder
    -> GrainReminderExtensions
    -> IReminderRegistry
    -> ReminderRegistry
    -> IReminderService
    -> LocalReminderService
    -> IReminderTable.UpsertRow
    -> 本地 reminder 记录 + 本地 async timer
    -> 周期性刷新 reminder table
    -> 到点后调用 IRemindable.ReceiveReminder
```

这两条线名字都像 timer，实际不是一回事。

本地 timer 的核心是“回调和 activation 绑死在一起”。

Reminder 的核心是“先把调度信息存下来，再让服务自己把它拉起来执行”。

---

## 2. 本地 Timer 这条线：它只是 activation 里的一个回调器

关键文件：

- `src/Orleans.Core.Abstractions/Core/Grain.cs`
- `src/Orleans.Core.Abstractions/Core/IGrainBase.cs`
- `src/Orleans.Runtime/Timers/TimerRegistry.cs`
- `src/Orleans.Runtime/Timers/GrainTimer.cs`
- `src/Orleans.Runtime/Core/SystemTarget.cs`
- `src/Orleans.Core.Abstractions/Timers/ITimerRegistry.cs`

### 2.1 `RegisterTimer` 其实已经是旧接口了

在 `Grain` 基类里，老的 `RegisterTimer(...)` 现在已经标了 obsolete：

- 它只是一个薄包装
- 真正干活的是 `Runtime.TimerRegistry.RegisterTimer(...)`

新路子是 `RegisterGrainTimer(...)`。

这个变化挺说明问题的。

Orleans 以前把 timer 当成一个“简单回调”看，后来又补上了 keep alive、interleave、CancellationToken 这些语义，最后就把老 API 留在那儿当历史壳了。

### 2.2 `TimerRegistry` 做的事很单纯

`ITimerRegistry` 有两个入口：

- `RegisterTimer(...)`
- `RegisterGrainTimer(...)`

底层都交给 `TimerRegistry`。

它做的事就三步：

1. new 一个 `GrainTimer` 或 `InterleavingGrainTimer`
2. 通知 activation 记录它
3. 调 `Change(dueTime, period)` 开始跑

所以 timer 的本质不是“一个后台线程在那儿睡觉”。

它是 Orleans runtime 里挂在某个 context 上的一个对象，到了点会自己把一个本地消息打回这个 context。

### 2.3 `GrainTimer` 不是直接回调函数，而是先造一条本地消息

`GrainTimer` 这块是最关键的。

它到点以后不会直接在 `System.Threading.Timer` 的线程里执行你的 callback。

它先做的是：

1. 造一个 `Message`
2. 把 body 填成一个 `TimerTickInvoker`
3. 标记 `IsLocalOnly = true`
4. 标记 `IsKeepAlive` 和 `IsAlwaysInterleave`
5. 把消息直接塞回当前 grain context 的 `ReceiveMessage(...)`

这一步很重要。

意思是 timer 的 callback 不是“外面偷偷调你一下”，而是“先变成一条 Orleans 消息，再按 grain 自己的调度规则进来”。

所以你看到的 timer callback，依然是在 Orleans 的执行模型里跑的，不是裸线程回调。

### 2.4 下一次 tick 什么时候排

`GrainTimer` 的节奏也很明确：

- 当前 callback 还没结束，下一次 tick 不会提前排
- callback 结束后，才决定下一次是按原 period 还是按新 dueTime
- `Change(...)` 如果发生在 firing 过程中，会延后到这次 tick 完成后再生效

这意味着本地 timer 默认不会并发重入自己。

你要它和其他请求、其他 timer 怎么交错，靠的是：

- `Interleave`
- grain 自己的 reentrancy 规则

### 2.5 本地 timer 会跟着 activation 一起死

这个地方不要绕。

`IGrainBase` 的文档已经写得很直白了：

- grain 被 deactivated 的时候，timer 会被丢掉
- `Dispose()` 以后也不会再有后续 tick

也就是说，本地 timer 没有持久化语义。

它只适合那些“这个 activation 活着就行”的任务，比如：

- 轮询
- 延迟执行
- 活跃期内的节拍器

如果你想要的是“这个时间到了，不管进程重启还是 activation 换人，都要尽量继续交付”，那就不是它。

---

## 3. Reminder 这条线：它是持久服务，不是普通 timer

关键文件：

- `src/Orleans.Reminders/GrainReminderExtensions.cs`
- `src/Orleans.Reminders/Timers/IReminderRegistry.cs`
- `src/Orleans.Reminders/ReminderService/ReminderRegistry.cs`
- `src/Orleans.Reminders/SystemTargetInterfaces/IReminderService.cs`
- `src/Orleans.Reminders/ReminderService/LocalReminderService.cs`
- `src/Orleans.Reminders/SystemTargetInterfaces/IReminderTable.cs`
- `src/Orleans.Reminders/Options/ReminderOptions.cs`

### 3.1 用户看到的是 grain 扩展，背后其实是服务调用

你在 grain 代码里写的通常是：

- `RegisterOrUpdateReminder(...)`
- `UnregisterReminder(...)`
- `GetReminder(...)`
- `GetReminders(...)`

这些扩展方法都在 `GrainReminderExtensions` 里。

表面上像是在操作 grain 自己的一个能力，实际上它们最后都会转到：

- `IReminderRegistry`

然后再转成对 `IReminderService` 的调用。

所以 reminder 的调用路径不是“grain 自己维护一个本地表”，而是“grain 通过 registry 去找 reminder service”。

### 3.2 `ReminderRegistry` 先做输入校验，再找 service

`ReminderRegistry` 这层负责的不是业务逻辑，是入口守门。

它会先检查：

- `dueTime` 不能是 `InfiniteTimeSpan`
- `period` 不能是 `InfiniteTimeSpan`
- `dueTime` 和 `period` 不能是负数
- `period` 不能小于 `MinimumReminderPeriod`
- reminder 名字不能空

然后它还会确认 reminder 服务真的配置好了。

如果提醒存储没挂上，直接报配置错误，不会默默帮你降级。

这个设计很现实。

Reminder 是持久语义，不是可有可无的装饰品。没存储就别装作自己能可靠工作。

### 3.3 `IReminderTable` 是真正的持久化抽象

`IReminderService` 不直接操作 Azure Table、SQL、DynamoDB 之类的实现，它只依赖：

- `IReminderTable`

这个接口定义得很清楚：

- `StartAsync`
- `ReadRow`
- `ReadRows`
- `UpsertRow`
- `RemoveRow`
- `StopAsync`

也就是说 reminder 这条线是“服务 + 表”的组合，不是单个对象。

这也是它和本地 timer 最大的不同点之一：

本地 timer 不需要持久层，Reminder 必须有。

### 3.4 `LocalReminderService` 才是真正跑调度的那个人

`LocalReminderService` 这个名字容易误导。

它不是“本地版 reminder 的备用实现”，而是每个 silo 上负责当前责任区间的 reminder 服务实例。

它做的事情很多：

- 读取 reminder table
- 按 consistent ring 计算自己负责的范围
- 维护本地 `localReminders` 字典
- 启动每个 reminder 对应的本地 async timer
- 定期刷新 reminder table
- 在 range 变化时增删本地提醒

这东西已经不是一个“service 接口”那么简单了，它更像一个小型调度器。

### 3.5 `LocalReminderService` 里的 tick 是怎么跑的

每条 reminder 先变成一个 `LocalReminderData`。

里面保存了：

- `GrainId`
- `ReminderName`
- `Period`
- `StartAt`
- `ETag`
- `IRemindable` 的 grain reference

然后它自己起一个 `IAsyncTimer` 循环：

1. 算下一次 due time
2. `NextTick(...)`
3. 到点后调用 `ReceiveReminder(...)`
4. 再按 `FirstTickTime + Period` 重新算下一次

这里的 `IAsyncTimer` 不是 grain timer。

它就是服务内部的异步等待计时器，用来做后台轮询和 reminder tick 排程。

### 3.6 Reminder tick 传给 grain 的不是普通参数，而是 `TickStatus`

`ReceiveReminder(string reminderName, TickStatus status)` 的 `TickStatus` 里有三样东西：

- `FirstTickTime`
- `Period`
- `CurrentTickTime`

这不是装饰。

它是给应用层自己算“漏了多少 tick”用的。

也就是说，Orleans 不保证把每个理论 tick 都补齐给你，但它会把足够的信息交给你，让你自己判断有没有漏。

这个语义比“定时器必达”朴素得多，也真实得多。

### 3.7 Reminder 的恢复不是靠重建对象，而是靠重读表

`LocalReminderService` 的启动过程是分两步的：

1. 先确认 table 能访问
2. 再后台开始读表、刷新、补本地提醒

它还会按默认 5 分钟周期刷新 reminder list。

range 变化、服务重启、表里数据变化，最后都要收敛成一件事：

- 重新读 reminder table
- 重新判断本 silo 是否有责任
- 该开就开，`ETag` 不对就停掉重来

这就是 reminder 的恢复语义。

它不是把 timer 状态存在内存里等你回来。

它是把状态放在表里，回来以后重新对齐。

---

## 4. 两条链路到底差在哪

### 4.1 生命周期不同

本地 timer 绑定的是 activation 生命周期。

Reminder 绑定的是 grain 身份和全局 reminder table。

所以：

- activation 没了，timer 没了
- silo 重启了，timer 没了
- reminder table 还在，Reminder 还能回来

### 4.2 交付保证不同

本地 timer 的保证很朴素：

- callback 只会在当前 activation 上跑
- 下一次 tick 不会和自己并发
- 但如果 activation 没了，什么都没有

Reminder 的保证更像：

- 同一个 reminder 只会由一个 activation 接收
- 但不承诺每个理论 tick 都精确命中
- 如果系统延迟或故障，`TickStatus` 允许应用自己判断漏了多少

所以 Reminder 不是“更强的 timer”，它是“另一种语义”。

### 4.3 状态落点不同

本地 timer 的状态主要在 activation 内存里。

Reminder 的状态主要在 table 里。

这直接决定了恢复方式：

- 本地 timer 只能重建
- Reminder 可以读回再继续

### 4.4 API 风格不同

本地 timer 是：

- `RegisterTimer(...)`
- `RegisterGrainTimer(...)`
- `Dispose()`
- `Change(...)`

Reminder 是：

- `RegisterOrUpdateReminder(...)`
- `UnregisterReminder(...)`
- `GetReminder(...)`
- `GetReminders(...)`

前者是实例级控制。

后者是服务级 CRUD。

### 4.5 失败后的处理也不同

本地 timer 的 callback 抛异常，通常只是记日志，不会自己把整个调度系统搞崩。

Reminder 的 tick 失败也会记日志，但它还有一个更麻烦的问题：

- repeated failures 到底该怎么处理

源码里基本是留了个注释，没给一个特别硬的策略。

这就很 Orleans。

能跑就先跑，语义边界靠文档和注释兜着。

---

## 5. 这两条线里，最不够干净的地方

### 5.1 `RegisterTimer` 和 `RegisterGrainTimer` 的并存让 API 看起来有点乱

老接口还在，新的推荐接口也在。

语义上能懂，但读源码时会先被一堆 overload 绕一下。

### 5.2 本地 timer 和 reminder 都叫 timer，容易把人看晕

一个是激活态内的短命计时器。

一个是持久服务里的提醒调度。

名字太像，语义差太远。

### 5.3 `LocalReminderService` 做的事太多了

它同时在管：

- table I/O
- ring 责任
- 本地调度
- 初始化重试
- range 变化
- 失败恢复
- 监控指标

这已经不是一个 service 了，这是一个小型系统。

你想复刻的话，最好别原样照搬成一坨。

### 5.4 Reminder 的交付语义不够“硬”

它不是 exactly-once。

也不是严格意义上的 at-least-once 保证。

它更像“持久调度 + 尽量送达 + 应用自己算漏 tick”。

这个语义其实挺实用，但对第一次看的人来说不够直观。

### 5.5 `IReminderRegistry` 这层只是为了把 grain 入口接到 service 上

这个抽象是有用的，但它也把路径又拉长了一层：

- grain 扩展
- registry
- grain service client
- service
- table

看起来就很 Orleans。

能用，但不轻。

---

## 6. 如果你要自己复刻，我会怎么拆

### 6.1 把本地 timer 和持久 reminder 彻底分成两套命名

不要再叫它们都叫 timer。

一个叫 `ActivationTimer`，一个叫 `ReminderSchedule`，会清楚很多。

### 6.2 把 reminder service 拆薄

至少拆成三层：

- 责任区间计算
- reminder table 同步
- local tick 执行

现在 `LocalReminderService` 太像一个“大杂烩入口”。

### 6.3 明确交付语义

最好别让“保证”写得太模糊。

如果你的目标和 Orleans 一样，那就老老实实写：

- 不保证每个理论 tick 都补齐
- 保证持久化
- 保证只向一个当前负责的 activation 交付
- 允许应用按 `TickStatus` 计算漏失

### 6.4 把失败策略写成显式策略

Orleans 这里对 repeated failure 的处理偏软。

如果是你自己复刻，我建议把它做成配置项，而不是注释。

比如：

- 继续重试
- 指数退避
- 超过阈值后暂停 reminder
- 记录死信

这样后面维护起来会轻很多。

---

## 7. 这一篇最该反复看的源码文件

建议按这个顺序读：

1. `src/Orleans.Core.Abstractions/Core/Grain.cs`
2. `src/Orleans.Core.Abstractions/Core/IGrainBase.cs`
3. `src/Orleans.Core.Abstractions/Timers/ITimerRegistry.cs`
4. `src/Orleans.Runtime/Timers/TimerRegistry.cs`
5. `src/Orleans.Runtime/Timers/GrainTimer.cs`
6. `src/Orleans.Runtime/Core/SystemTarget.cs`
7. `src/Orleans.Reminders/GrainReminderExtensions.cs`
8. `src/Orleans.Reminders/Timers/IReminderRegistry.cs`
9. `src/Orleans.Reminders/ReminderService/ReminderRegistry.cs`
10. `src/Orleans.Reminders/SystemTargetInterfaces/IReminderService.cs`
11. `src/Orleans.Reminders/SystemTargetInterfaces/IReminderTable.cs`
12. `src/Orleans.Reminders/ReminderService/LocalReminderService.cs`
13. `src/Orleans.Reminders/Options/ReminderOptions.cs`

如果你把这 13 个文件顺下来，再回头看 Orleans 里谁在做调度，谁在做持久化，谁在做恢复，基本就不会再混了。

---

## 8. 这一篇的落点

把这一篇压成一句话，就是：

Orleans 里看起来都像“定时”，但本地 timer 和 reminder 其实是两套不同语义的系统。

- 本地 timer 解决的是“这个 activation 活着时，怎么安排回调”
- Reminder 解决的是“这个 grain 身份，怎么跨重启持续收到提醒”

前者轻，后者稳。

前者靠 activation，后者靠表和服务。

这也是 Orleans 很多地方的共同特点：名字看着像，底层其实是两套逻辑叠在一起。
