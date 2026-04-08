# Grain 生命周期与回收：`ActivationData`、`Catalog`、`ActivationCollector` 是怎么收尾的（第十四篇）

这一篇讲 Orleans 里最容易被低估的一块：一个 grain 不是“new 出来就一直活着”，它有创建、激活、保活、收回、迁移、销毁这整条链。

先把结论说直白一点：

- `ActivationData` 是这条链的中心，绝大多数生命周期状态都落在它身上。
- `Catalog` 负责创建、迁移、主动停机和统一收尾，不只是一个“查 activation 的字典”。
- `ActivationCollector` 负责 idle collection，按时间轮和内存压力挑人下手。
- `ActivationWorkingSet` 负责维护“最近活跃”的视图，给 collector 提供候选集。
- `CollectionAgeLimit` 不是单个配置项，而是一套来源合并后的结果，既能来自 attribute，也能来自 manifest，也能来自全局配置。
- 迁移不是单独一条支线，它本质上是一次带 `Migrating` 原因的 deactivation，外加一段 dehydration / rehydration。

如果把这篇压成一句话，就是：

> Orleans 的生命周期不是“激活一次、调用很多次、最后销毁”这么简单，而是“创建、注册、激活、空闲、回收、必要时迁移、最后清理目录和资源”的完整运行时协议。

---

## 1. 先把整条主链压成一张图

```text
创建 activation
  -> Catalog.GetOrCreateActivation(...)
  -> ActivationDataActivatorProvider
  -> new ActivationData(...)
  -> Task.Factory.StartNew(..., context.ActivationTaskScheduler)
  -> ActivationData.Start(...)
  -> grain instance created
  -> 如果可迁移/可回收，还会挂上生命周期参与者和 timer/collector 关联

激活流程
  -> 目录注册（如需要）
  -> State = Activating
  -> IGrainLifecycle.OnStart(...)
  -> Grain.OnActivateAsync(...)
  -> State = Valid
  -> ActivationWorkingSet.OnActivated(...)
  -> GrainLifecycleEvents.EmitActivated(...)

空闲收回
  -> ActivationCollector/ActivationWorkingSet 发现 idle
  -> ActivationData.Deactivate(...)
  -> State = Deactivating
  -> CancelPendingOperations()
  -> ScheduleOperation(Command.Deactivate)
  -> FinishDeactivating(...)
  -> Grain.OnDeactivateAsync(...)
  -> IGrainLifecycle.OnStop(...)
  -> 目录注销（如需要）
  -> UnregisterMessageTarget()
  -> DisposeTimers()
  -> DisposeAsync()
  -> GrainLifecycleEvents.EmitDeactivated(...)

迁移
  -> Migrate(...)
  -> DehydrationContext
  -> Deactivate(reason: Migrating)
  -> FinishDeactivating(...)
  -> OnDehydrate(...)
  -> migrationManager.MigrateAsync(...)
  -> 新 activation Rehydrate(...)
  -> OnRehydrate(...)
  -> 再进入 Activate(...)
```

这条链里最关键的一点是：

`Activate`、`Deactivate`、`Rehydrate` 不是三套互相独立的流程，它们是同一个 activation 生命周期里的不同阶段。

---

## 2. 先看 activation 是怎么被造出来的

关键文件：

- `src/Orleans.Runtime/Catalog/Catalog.cs`
- `src/Orleans.Runtime/Activation/ActivationDataActivatorProvider.cs`
- `src/Orleans.Runtime/Catalog/ActivationData.cs`
- `src/Orleans.Runtime/Catalog/GrainTypeSharedContext.cs`

### 2.1 `Catalog.GetOrCreateActivation(...)` 是总入口

`Catalog` 不是一个单纯的消息路由器，它是 local silo 上 activation 的总控。

当它要创建新 activation 时，会做这些事：

1. 先查 `ActivationDirectory` 有没有现成的本地 activation
2. 如果没有，就按 grain id 选一个 striped lock
3. 再确认一次目录里没有别人已经建好
4. 只有在本 silo 还是 `Active` 状态时才真正创建
5. 调 `grainActivator.CreateInstance(address)` 造出 `ActivationData`
6. 记录到 `ActivationDirectory`
7. 启动 activation trace span
8. 如果有 rehydration context，先 `Rehydrate(...)`
9. 再 `Activate(requestContext)`

也就是说，创建 activation 时，`Catalog` 先负责“造壳 + 挂目录 + 拉起生命周期”，不是只 new 一个对象就完事。

### 2.2 `ActivationDataActivatorProvider` 把调度器一起挂上

关键文件：

- `src/Orleans.Runtime/Activation/ActivationDataActivatorProvider.cs`

它创建 `ActivationData` 时，不只是传一个地址。

它还会同时挂：

- `WorkItemGroup`
- `ActivationTaskScheduler`
- `IGrainActivator`
- `GrainTypeSharedContext`

真正跑起来以后，`ActivationData` 不是“一个普通对象”，而是一个自己带 scheduler、队列、生命周期、目录信息、timer 入口的运行时单元。

### 2.3 `GrainTypeSharedContext` 决定这类 grain 的回收门槛

关键文件：

- `src/Orleans.Runtime/Catalog/GrainTypeSharedContext.cs`

`CollectionAgeLimit` 就是在这里算出来的。

优先级很明确：

1. 先看 grain manifest 里的 `IdleDeactivationPeriod`
2. 再看 `[CollectionAgeLimit]` 或 `[KeepAlive]`
3. 再看 `GrainCollectionOptions.ClassSpecificCollectionAge`
4. 最后回落到全局 `GrainCollectionOptions.CollectionAge`

所以这个值不是一个简单配置项，而是“每个 grain type 的最终收回门槛”。

如果配置成无限期，`ActivationData.IsExemptFromCollection` 就会直接返回 true，collector 也不会碰它。

### 2.4 `GrainLifecycle` 是 activation 的生命周期观察对象

关键文件：

- `src/Orleans.Runtime/Catalog/GrainLifecycle.cs`
- `src/Orleans.Core.Abstractions/Runtime/GrainLifecycleStage.cs`

`GrainLifecycleStage` 里最关键的几个点是：

- `SetupState`
- `Activate`

这意味着 grain 的生命周期不是只看 `OnActivateAsync` 和 `OnDeactivateAsync`，中间还可以插 `SetupState` 这样的阶段。

`Grain<TState>` 的构造函数就会在 `SetupState` 阶段挂一个 observer，用来把 state storage 接起来。

这就是为什么 Orleans 的 activation 生命周期看起来像一条线，实际上里面塞了不少内部 hook。

---

## 3. `OnActivateAsync` 到底什么时候跑

关键文件：

- `src/Orleans.Core.Abstractions/Core/Grain.cs`
- `src/Orleans.Core.Abstractions/Core/IGrainBase.cs`
- `src/Orleans.Runtime/Catalog/ActivationData.cs`

### 3.1 `OnActivateAsync` 不是最早的第一步

`ActivationData.ActivateAsync(...)` 真正做的顺序是：

1. 如果需要，先注册 grain directory
2. 把 `State` 切到 `Activating`
3. `RequestContextExtensions.Import(...)`
4. 跑 `IGrainLifecycle.OnStart(...)`
5. 再跑用户的 `Grain.OnActivateAsync(...)`
6. 成功后把 `State` 切到 `Valid`
7. 加入 `ActivationWorkingSet`
8. 发 `GrainLifecycleEvents.EmitActivated(...)`

所以 `OnActivateAsync` 不是“构造完成后马上就跑”的那种方法，它前面还有目录注册和 lifecycle stage。

### 3.2 `Grain<TState>` 的 state 其实更早就能接进来

`Grain<TState>` 的生命周期 observer 会在 `SetupState` 阶段先把 storage 设好。

如果 activation 是迁移过来的，它还能先走 `OnRehydrate(...)`，然后再决定要不要读存储。

这就是 Orleans 的一个很典型做法：

先把运行时骨架装好，再让用户代码进场。

### 3.3 `OnActivateAsync` 失败时不是简单抛出去

如果 `OnActivateAsync` 里出错，`ActivationData` 会区分几种情况：

- 取消导致的失败
- 激活过程本身失败
- 业务异常

有些会触发 `Deactivate(...)`，有些会直接记成错误活动。

这也是 Orleans 比“普通对象初始化”更重的地方：

activation 的失败是运行时事件，不只是构造函数抛异常。

---

## 4. `OnDeactivateAsync` 是怎么被触发的

关键文件：

- `src/Orleans.Runtime/Catalog/ActivationData.cs`
- `src/Orleans.Core.Abstractions/Core/Grain.cs`
- `src/Orleans.Core.Abstractions/Core/IGrainBase.cs`

### 4.1 主动退场和自然回收最后都收敛到 `Deactivate(...)`

用户侧有两个最常见入口：

- `DeactivateOnIdle()`
- `DelayDeactivation(...)`

`DeactivateOnIdle()` 是主动告诉 runtime：“这次调用结束后就让我退场。”

`DelayDeactivation(...)` 则是反过来告诉 runtime：“别太快收我，我要多活一会儿。”

这两个 API 都不是直接销毁对象，而是影响 activation 的后续生命周期判断。

### 4.2 `Deactivate(...)` 先把状态改掉，再排队执行真正的收尾

`ActivationData.Deactivate(...)` 会先做这些事：

1. 进入 `Deactivating`
2. 记录 deactivation reason
3. 记录 deactivation start time
4. `GrainLifecycleEvents.EmitDeactivating(...)`
5. `CancelPendingOperations()`
6. `ActivationWorkingSet.OnDeactivating(...)`
7. 排一个 `Command.Deactivate`

真正的 `OnDeactivateAsync(...)` 不在这一步跑，而是在后面的 `FinishDeactivating(...)` 里跑。

这就把“发起退场”和“完成退场”拆开了。

### 4.3 `FinishDeactivating(...)` 才是完整收尾

`FinishDeactivating(...)` 的顺序比较固定：

1. 先停 timers
2. 如果之前是 `Valid`，再调用 `Grain.OnDeactivateAsync(...)`
3. 再调 `IGrainLifecycle.OnStop(...)`
4. 如果这是 migration，走 dehydration / migration manager
5. 如果需要，注销 grain directory
6. 不管怎样都清理 message target
7. 再 `DisposeAsync()`
8. 最后发 `GrainLifecycleEvents.EmitDeactivated(...)`
9. 把 `Deactivated` 的 `TaskCompletionSource` 完成掉

这里有个很实在的点：

`OnDeactivateAsync` 失败了，不会阻止后面的销毁继续跑。Orleans 更看重“把 activation 收干净”，而不是让退场链卡死在用户回调里。

### 4.4 timers 是先停的

`DisposeTimers()` 在 `OnDeactivateAsync` 之前就会先做。

这意味着：

- 退场一旦开始，timer 不会继续往回打消息
- 清理逻辑优先于用户 deactivation 回调

这点挺关键，因为它直接决定了“deactivating 时还能不能继续跑回调”的边界。

---

## 5. idle collection 是怎么决定谁该被收的

关键文件：

- `src/Orleans.Runtime/Catalog/ActivationCollector.cs`
- `src/Orleans.Runtime/Catalog/ActivationWorkingSet.cs`
- `src/Orleans.Runtime/Configuration/Options/GrainCollectionOptions.cs`
- `src/Orleans.Runtime/Configuration/Validators/GrainCollectionOptionsValidator.cs`
- `src/Orleans.Core/Configuration/CollectionAgeLimitAttribute.cs`

### 5.1 `ActivationWorkingSet` 先维护“最近活跃”的候选集

`ActivationWorkingSet` 不是 collector 本身，它更像一个最近活跃成员表。

它会在这些时机更新状态：

- activation 被 `OnActivated(...)`
- activation 变成 active
- activation 被 evicted
- activation 开始 deactivating
- activation 已经 deactivated

`ActivationCollector` 订阅了这个 working set，所以它不是全量扫所有 activation，而是先吃一份“最近活跃”的视图。

### 5.2 `CollectionAgeLimit` 和 `CollectionQuantum` 不是一个概念

`GrainCollectionOptions` 里最重要的两个参数是：

- `CollectionAge`
- `CollectionQuantum`

`CollectionAge` 是“空闲多久可以收”。

`CollectionQuantum` 是“收集器每次走多粗的时间片”。

验证器也很明确：`CollectionAge` 必须大于 `CollectionQuantum`，类级别的 `CollectionAgeLimit` 也一样。

### 5.3 `ActivationCollector` 用 ticket + bucket 来排队收回

收集器不是每次都把全量 activation 过一遍。

它会给每个 activation 发一个 `CollectionTicket`，再按 ticket 放进 bucket。

运行时流程大概是：

1. `OnAdded(...)` 时给新 activation 安排收集票
2. `OnActive(...)` 时只标记 active，不急着做重活
3. `OnDeactivating(...)` 时取消收集
4. 定时扫描 `ScanStale()` 或 `ScanAll(...)`
5. 如果 activation 真的足够久没动，就加入收回列表
6. 再调用 `DeactivateActivationsFromCollector(...)`

这套机制的意思很直白：

不是每次看到一个 idle activation 就马上收，而是先做一个时间轮，减少扫描成本。

### 5.4 `DelayDeactivation(...)` 会直接影响 collector 的判断

`ActivationData.DelayDeactivation(...)` 其实就是给 activation 挂一个 `KeepAliveUntil`。

如果你传的是：

- 正数：延后这段时间再考虑收
- `TimeSpan.Zero`：撤销之前的保活
- `Timeout.InfiniteTimeSpan`：直接等于无限保活

collector 看到 `KeepAliveUntil` 还没到，就会跳过这次收回。

这也是为什么 `DelayDeactivation` 会直接影响 idle collection，而不是只影响某个局部标记。

### 5.5 `DeactivateOnIdle()` 是“主动退场”，不是“等 collector”

这是很多人容易混的地方。

`DeactivateOnIdle()` 走的是：

- 立即把 activation 标成要退场
- 等当前调用结束后完成 deactivation

它不是“等收集器下次扫到我再说”。

所以它和 `DelayDeactivation(...)` 正好是反方向：

- 一个是尽快退
- 一个是尽量活

---

## 6. 迁移其实就是一套带数据搬运的 deactivation

关键文件：

- `src/Orleans.Runtime/Catalog/ActivationData.cs`
- `src/Orleans.Core.Abstractions/Lifecycle/IGrainLifecycle.cs`
- `src/Orleans.Runtime/Catalog/ActivationMigrationManager.cs`

### 6.1 `Migrate(...)` 先挂 dehydration context，再走 deactivation

`ActivationData.Migrate(...)` 的逻辑不是单独开一条迁移通道，而是：

1. 先确认 activation 还处于可迁移状态
2. 创建 `DehydrationContext`
3. 如果还没开始 `Deactivating`，就调用 `Deactivate(reason: Migrating)`

也就是说，迁移本质上就是一场特殊的 deactivation。

### 6.2 `FinishDeactivating(...)` 里决定到底是真退还是迁移

如果 deactivation 过程中发现：

- 没出错
- 有 `DehydrationContext`
- 迁移管理器存在
- 还没取消

那就会走 `StartMigrationAsync(...)`。

这一步会：

1. 先决定目标 silo
2. 把 request context 塞回去
3. 调 `OnDehydrate(...)`
4. 交给 `migrationManager.MigrateAsync(...)`
5. 更新 grain directory cache

所以迁移和普通退场的区别，不是“调用了另一个方法”，而是“deactivation 结尾有没有把状态和上下文带走”。

### 6.3 新 activation 的 `Rehydrate(...)` 只在创建阶段吃上下文

`RehydrateInternal(...)` 只有在 `State == Creating` 时才会真正接收上下文。

它会做这些事：

- 如果迁移上下文里带了旧 registration，就记到 `PreviousRegistration`
- 先让生命周期里的 migration participants `OnRehydrate(...)`
- 再让 grain instance 自己的 `IGrainMigrationParticipant.OnRehydrate(...)`

这个顺序和 `OnDehydrate(...)` 正好是反过来的。

### 6.4 `Grain<TState>` 这里也会参与迁移

`Grain<TState>` 的 lifecycle observer 会把 state storage 也接到 migration participant 里。

这意味着迁移不只是搬 grain instance 自己的字段，持久化状态也会跟着这套机制一起走。

这点挺 Orleans。

它不是把“state storage”和“activation migration”拆成两套完全独立的系统，而是强行绑在同一个 lifecycle 上。

---

## 7. `Catalog`、`ActivationDirectory`、`ActivationCollector` 这三个东西怎么配合

关键文件：

- `src/Orleans.Runtime/Catalog/Catalog.cs`
- `src/Orleans.Runtime/Catalog/ActivationDirectory.cs`
- `src/Orleans.Runtime/Catalog/ActivationCollector.cs`

### 7.1 `ActivationDirectory` 只是本地事实表

`ActivationDirectory` 就是一个 `GrainId -> IGrainContext` 的本地字典。

它负责：

- `FindTarget`
- `RecordNewTarget`
- `RemoveTarget`

它不负责回收策略，也不负责生命周期。

### 7.2 `Catalog.UnregisterMessageTarget(...)` 会同时动目录和收集器

当 activation 真正要走的时候，`Catalog` 会：

- 从 `ActivationDirectory` 移除
- 让 `ActivationCollector` 停止收这个 activation

这说明“目录注销”和“回收链”不是两条完全独立的线。

### 7.3 `Catalog.DeactivateAllActivations(...)` 是 silo 收尾时的总开关

当 silo 自己要停的时候，`Catalog` 会统一把本地 activations 退掉。

所以从系统级角度看：

- `ActivationCollector` 负责“正常情况下的闲置回收”
- `Catalog` 负责“运行时和关机时的统一退场”

这两个职责是叠着的。

---

## 8. 这条链里我觉得不够干净的地方

### 8.1 生命周期状态分散得太开

一个 activation 的状态，不只在 `ActivationState` 里。

它还散在这些地方：

- `ActivationData.State`
- `CollectionTicket`
- `KeepAliveUntil`
- `DeactivationReason`
- `DeactivationStartTime`
- `ForwardingAddress`
- `PreviousRegistration`
- `ActivationWorkingSet` 里的成员状态

这就导致你读生命周期时，不能只看一个枚举。

### 8.2 回收不是一套单独系统，而是好几层拼起来的

idle collection 其实由这几层一起组成：

- 配置
- working set
- collector
- activation 自己的 idleness/keepalive 状态
- 目录和消息重定向

优点是能细控，缺点是不好一眼看懂。

### 8.3 迁移被塞进 deactivation 里，逻辑不算干净

迁移其实是一个相当大的动作，但 Orleans 把它放在 deactivation 的尾巴里做。

这样做能复用大量退场逻辑，但代价是：

- 迁移和普通销毁的边界有点糊
- 读代码时要一直在“是真退场还是去别处继续活”之间切换

### 8.4 `Catalog` 既像运行时控制器，又像资源回收器

`Catalog` 不只管创建。

它还管：

- 注册
- 退场
- 迁移
- 目录清理
- 全量停机

这个对象太像“运行时总控台”了，后期很容易越长越大。

---

## 9. 如果你要自己复刻，我会怎么拆

如果目标是复刻一版更干净的，我会至少拆成四块：

### 9.1 Activation Core

只负责：

- activation 状态
- request 队列
- turn 调度
- OnActivate/OnDeactivate 的执行顺序

### 9.2 Collection Manager

只负责：

- idle 票据
- working set
- time wheel
- memory pressure shedding

### 9.3 Migration Manager

只负责：

- dehydration / rehydration
- 迁移目标选择
- 上下文搬运
- 目录更新

### 9.4 Directory Facade

只负责：

- 本地 activation 注册
- 目录注销
- cache invalidation
- 迁移后地址更新

这样拆完以后，生命周期就不会全压在 `ActivationData` 一个对象上。

---

## 10. 这一篇最该反复看的源码文件

建议按这个顺序读：

1. `src/Orleans.Runtime/Catalog/ActivationData.cs`
2. `src/Orleans.Runtime/Catalog/Catalog.cs`
3. `src/Orleans.Runtime/Catalog/ActivationCollector.cs`
4. `src/Orleans.Runtime/Catalog/ActivationWorkingSet.cs`
5. `src/Orleans.Runtime/Catalog/GrainTypeSharedContext.cs`
6. `src/Orleans.Core.Abstractions/Core/Grain.cs`
7. `src/Orleans.Core.Abstractions/Core/IGrainBase.cs`
8. `src/Orleans.Core.Abstractions/Core/IGrainContext.cs`
9. `src/Orleans.Core.Abstractions/Runtime/GrainLifecycleStage.cs`
10. `src/Orleans.Core/Configuration/CollectionAgeLimitAttribute.cs`
11. `src/Orleans.Runtime/Configuration/Options/GrainCollectionOptions.cs`
12. `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`

如果你把这 12 个文件顺下来，再回头看前面几篇的消息、调度、目录和 migration，Orleans 的 activation 生命周期就基本不会再是黑盒了。

---

## 11. 这一篇的落点

把这一篇压成一句话，就是：

Orleans 的 grain 生命周期，不是“创建后一直活着，直到用户手动销毁”，而是一套由 `Catalog`、`ActivationData`、`ActivationCollector`、`ActivationWorkingSet`、`GrainLifecycle`、`CollectionAgeLimit` 和 migration 机制一起拼出来的收尾协议。

它能跑得很稳，但也确实不算干净。

因为一件本来应该是“激活对象的生命周期管理”的事，被拆成了多层对象、多种票据、多套 observer、以及一串带副作用的收尾动作。

下一篇如果继续写，我建议直接接：

- `Transactions` 为什么会把生命周期、状态、回收和一致性绑得更紧

或者换个方向，补一篇：

- Orleans 的诊断和遥测：`Activity`、`DiagnosticListener`、`Counters` 是怎么散进运行时的

