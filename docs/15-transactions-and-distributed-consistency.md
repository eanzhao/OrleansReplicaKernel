# Transactions 与分布式一致性：Orleans 的事务链路到底是怎么拼起来的（第十五篇）

这一篇讲 Orleans 的事务系统。

先把话说直一点：Orleans 这里不是一套“数据库式”的全局事务引擎。它更像是把事务拆成了几层，各自干各自的活，然后靠上下文、锁、日志和恢复把一致性拼出来。

所以你会看到很多名字都很像，但职责并不重叠：

- `TransactionContext` 管调用链里的 ambient transaction。
- `TransactionRequestBase` 把 `[Transaction]` 方法包装成可序列化的请求。
- `TransactionClient` 负责创建、加入、抑制、收口事务。
- `TransactionAgent` 是每个 silo 上的协调器。
- `TransactionalState<T>`、`TransactionQueue<T>`、`ReadWriteLock<T>` 是参与者侧的状态、锁和日志。
- `ITransactionalStateStorage<T>` 才是真正落盘的事务状态存储。

如果把这篇先压成一句话，就是：

> Orleans 的事务不是一把锁，也不是一个 coordinator，而是一条从调用上下文，一路走到参与者日志和恢复的长链。

---

## 1. 先把整条链压成一张图

```text
用户写方法
  -> [Transaction] / [UseExclusiveLock]
  -> TransactionRequestBase / TransactionTaskRequest
  -> TransactionContext.AsyncLocal
  -> TransactionClient / TransactionAgent
  -> 生成或复用 TransactionInfo
  -> grain 调用真正执行
  -> TransactionalState<T> / TransactionCommitter<T>
  -> TransactionQueue<T> / ReadWriteLock<T>
  -> ITransactionalResourceExtension / ITransactionManagerExtension
  -> prepare / confirm / cancel / ping
  -> StorageBatch
  -> ITransactionalStateStorage<T>
  -> recovery 时重新装载 committed state + pending states + commit records
```

这条链里最容易误会的一点是：

事务语义不是只在一个地方完成的。

它在三个层面同时发生：

- 调用层决定这次要不要进事务。
- 协调层决定这次事务能不能收口。
- 参与者层决定这次事务有没有真正被锁住、写下去、恢复回来。

这也是 Orleans 事务看起来重的原因。它不是单点重，而是层层都带了一点。

---

## 2. 用户是怎么把事务接进来的

关键文件：

- `src/Orleans.Transactions/TransactionAttribute.cs`
- `src/Orleans.Transactions/TransactionContext.cs`
- `src/Orleans.Transactions/DistributedTM/TransactionClient.cs`
- `src/Orleans.Transactions/Hosting/TransactionsServiceCollectionExtensions.cs`
- `src/Orleans.Transactions/Hosting/SiloBuilderExtensions.cs`
- `src/Orleans.Transactions/Hosting/ClientBuilderExtensions.cs`

### 2.1 `[Transaction]` 不是装饰，它会改调用形态

`TransactionAttribute` 不是一个普通标记。

它带着这些东西：

- `TransactionOption`
- `UseExclusiveLock`
- `InvokableCustomInitializer("SetTransactionOptions")`
- `InvokableBaseType(...)` 到 `TransactionRequest` / `TransactionTaskRequest`

这意味着编译期生成器不会把它当普通方法属性忽略掉，而是会真的生成一个事务请求包装类。

所以 `[Transaction]` 的作用不是“说明这方法有事务语义”，而是“把这个方法改写成事务请求链的一部分”。

### 2.2 `TransactionOption` 的意思很直接

`TransactionOption` 的几种模式，可以粗暴理解成：

- `Create`：强制新开一个事务。
- `Join`：只能加入已有事务。
- `CreateOrJoin`：有就加入，没有就新开。
- `Suppress`：别把当前事务往下传。
- `Supported`：有 ambient transaction 就带下去，没有也无所谓。
- `NotAllowed`：有事务就直接拒绝。

这套模式和 `TransactionContext` 是绑在一起的。

`TransactionContext` 本身很简单，就是一个 `AsyncLocal<TransactionInfo>`。

也就是说，Orleans 事务的传播，先天就是“跟着 async 调用链跑”的。

### 2.3 `TransactionRequestBase` 才是关键包装器

事务请求的真正入口在：

- `src/Orleans.Transactions/TransactionAttribute.cs`

`TransactionRequestBase` 做的事很像一个小型事务中间件：

1. 看当前调用有没有 `TransactionInfo`。
2. 按 `TransactionOption` 决定要不要创建、复用、清空或拒绝事务。
3. 如果需要新事务，就调用 `TransactionAgent.StartTransaction(...)`。
4. 把 `TransactionContext` 塞回 `AsyncLocal`。
5. 执行真正的 `BaseInvoke()`。
6. 在返回前，把结果包装成 `TransactionResponse`。
7. 如果是自己新建的事务，就在返回前收口，调用 `Resolve` 或 `Abort`。

这层最容易看漏的点是 `TransactionResponse`。

它不是普通 response，而是一个“先把 response 和 transaction info 一起带回来”的外壳。这样请求端才能先看到事务信息，再决定是返回结果还是抛事务异常。

### 2.4 `TransactionClient` 是外部入口

`TransactionClient` 走的是更显式的编程模型：

- `Create`
- `Join`
- `CreateOrJoin`
- `Suppress`
- `Supported`
- `NotAllowed`

它会先拿 ambient transaction，再决定是新建、合并、清空还是直接拒绝。

这个地方很有代表性：

Orleans 的事务不是“只靠 attribute”，它也提供了一个显式 client API。两套入口最后还是落到同一个 `TransactionInfo` 上。

---

## 3. `TransactionInfo` 才是事务的核心状态

关键文件：

- `src/Orleans.Transactions/DistributedTM/TransactionInfo.cs`
- `src/Orleans.Transactions/DistributedTM/ParticipantId.cs`
- `src/Orleans.Transactions/TransactionalStatus.cs`

### 3.1 `TransactionInfo` 保存的是一笔事务的全部活动痕迹

它不是一个简单的 ID。

里面放的是：

- `TransactionId`
- `TimeStamp`
- `Priority`
- `IsReadOnly`
- `OriginalException`
- `Participants`
- `TryToCommit`
- `UseExclusiveLock`
- `PendingCalls`

这里面最重要的是 `Participants`。

它是一个 `Dictionary<ParticipantId, AccessCounter>`，记录了每个参与者被读了几次、写了几次。

这意味着 Orleans 不是到了提交时才“猜”参与者，而是在事务运行过程中就已经把读写足迹记下来了。

### 3.2 `ParticipantId` 是参与者身份，不是简单地址

`ParticipantId` 里有三样东西：

- `Name`
- `Reference`
- `SupportedRoles`

角色位分三种：

- `Resource`
- `Manager`
- `PriorityManager`

这不是多余。

因为 Orleans 的事务里，一个参与者有时只是资源，有时还兼任 manager，有时又得是 priority manager。写进 role 里，后面协商的时候就不用重新猜了。

### 3.3 `TransactionInfo` 还处理嵌套和合并

它有几个容易忽略但很关键的方法：

- `Fork()`
- `Join(...)`
- `ReconcilePending()`
- `MustAbort(...)`
- `RecordException(...)`

这几个方法说明了一件事：

Orleans 事务不是平铺的。

它允许事务嵌套、分叉、再合并。`PendingCalls` 会帮助判断有没有 orphan call，`MustAbort(...)` 会把原始异常和 orphan call 一起当成 abort 原因。

我把这理解为：它不是在追求一种最简的事务 API，而是在适配 Orleans 自己的调用图。

### 3.4 `TransactionalStatus` 是失败语义的主词表

`TransactionalStatus` 里最有用的不是 `Ok`，而是那些失败码：

- `PrepareTimeout`
- `CascadingAbort`
- `BrokenLock`
- `LockValidationFailed`
- `ParticipantResponseTimeout`
- `TMResponseTimeout`
- `StorageConflict`
- `PresumedAbort`
- `UnknownException`
- `AssertionFailed`
- `CommitFailure`

这套状态已经说明 Orleans 事务不是单一失败模型，而是把“到底卡在哪一步”拆得很细。

这有好处，也有代价。

好处是诊断信息很足。

代价是，代码阅读成本一下就上去了。

---

## 4. 分布式协调器怎么收口一笔事务

关键文件：

- `src/Orleans.Transactions/ITransactionAgent.cs`
- `src/Orleans.Transactions/DistributedTM/TransactionAgent.cs`
- `src/Orleans.Transactions/DistributedTM/TransactionClient.cs`
- `src/Orleans.Transactions/Abstractions/ITransactionManagerExtension.cs`
- `src/Orleans.Transactions/Abstractions/ITransactionalResourceExtension.cs`

### 4.1 `ITransactionAgent` 是每个 silo 的事务协调器

它做三件事：

- `StartTransaction(...)`
- `Resolve(...)`
- `Abort(...)`

源码里也写得很直白：

> There is one Transaction Agent per silo.

也就是说，事务协调器不是全局单例，更不是分布式数据库那种中心 TM。

它是每个 silo 都有一个本地协调入口。

### 4.2 `StartTransaction(...)` 先看系统是不是太忙

`TransactionAgent.StartTransaction(...)` 会先看 overload detector。

如果系统过载，就直接拒绝新事务。

这很实际，因为 Orleans 这套事务是靠锁和日志跑的，不是无限吞吐的黑洞。

它还会给事务生成：

- 一个新的 `Guid`
- 一个 causal clock timestamp

这里的 timestamp 不是装饰，它后面会参与锁分组、排序和冲突判断。

### 4.3 `Resolve(...)` 才是真正的协调过程

它先把事务按参与者分类。

然后分两类：

- 如果全是读，走 read-only commit。
- 如果有写，走 read-write 2PC。

读事务会给所有参与者发 `CommitReadOnly(...)`。

写事务会：

1. 先给除 manager 外的参与者发 `Prepare(...)`。
2. 再让 manager 执行 `PrepareAndCommit(...)`。
3. 如果失败，再看是不是要 `Cancel(...)`。

所以 Orleans 这里本质上是一个带逻辑时间戳和本地锁的两阶段提交实现，不是“事务标个记号然后大家自己乐意提交”的那种。

### 4.4 `Abort(...)` 是收尾动作，不是普通异常分支

Abort 不是在业务层随便 throw 一下就完了。

它会给所有参与者发 one-way `Abort(...)`，目的是：

- 释放锁
- 回滚写入
- 让参与者把挂起状态清掉

这也是为什么事务失败经常会伴随一串通知，而不是一个异常就结束。

### 4.5 事务扩展接口是参与者和 TM 的 RPC 面

`ITransactionalResourceExtension` 和 `ITransactionManagerExtension` 都是 grain extension。

它们对外暴露的其实就是事务协议：

- `Prepare`
- `Cancel`
- `Confirm`
- `CommitReadOnly`
- `PrepareAndCommit`
- `Prepared`
- `Ping`

这说明 Orleans 事务不是靠“直接调用对象方法”完成的，而是靠 grain extension 把事务协议包成分布式调用。

---

## 5. `TransactionalState<T>` 是怎么把事务挂到 grain 状态上的

关键文件：

- `src/Orleans.Transactions/Abstractions/TransactionalStateAttribute.cs`
- `src/Orleans.Transactions/Hosting/TransactionalStateAttributeMapper.cs`
- `src/Orleans.Transactions/Abstractions/ITransactionalStateFactory.cs`
- `src/Orleans.Transactions/State/TransactionalStateFactory.cs`
- `src/Orleans.Transactions/State/TransactionalState.cs`
- `src/Orleans.Transactions/State/TransactionQueue.cs`
- `src/Orleans.Transactions/State/ReaderWriterLock.cs`

### 5.1 `TransactionalStateAttribute` 是构造参数上的绑定点

用户一般会这么写：

```csharp
public MyGrain([TransactionalState("state", "storage")] ITransactionalState<MyState> state)
```

这个 attribute 只负责两件事：

- state name
- storage name

`TransactionalStateAttributeMapper` 再把它翻成 `TransactionalStateConfiguration`。

### 5.2 `TransactionalStateFactory` 把状态挂进生命周期

`TransactionalStateFactory.Create<TState>(...)` 会：

1. 用当前 grain context 造一个 `TransactionalState<TState>`
2. 把它订阅到 grain lifecycle
3. 让它自己在 `SetupState` 阶段初始化

这一步很关键，因为事务状态不是随手 new 一个对象就行。

它必须知道自己属于哪个 activation，才能把锁、恢复、资源工厂和生命周期连起来。

### 5.3 `TransactionalState<TState>` 不是一个普通 state 容器

它实现的是：

- `ITransactionalState<TState>`
- `ILifecycleParticipant<IGrainLifecycle>`

它的核心方法只有两个：

- `PerformRead(...)`
- `PerformUpdate(...)`

但这两个方法背后做了很多事：

- 取当前 `TransactionInfo`
- 校验是不是读写冲突
- 进入 `ReadWriteLock`
- 检查 transaction record 是否还活着
- 记录 read/write 足迹
- 读的时候深拷贝返回值
- 写的时候第一次写就深拷贝 state

也就是说，`TransactionalState<TState>` 不是“把状态包成一个对象”，而是“把状态变成事务参与者”。

### 5.4 `detectReentrancy` 是一个硬拦截

`PerformRead` 和 `PerformUpdate` 都会检查 `detectReentrancy`。

如果你在一次事务操作里又递归发起另一个读写操作，它会直接抛 `LockRecursionException`。

这说明 Orleans 事务并不是允许你在同一个事务上下文里无限重入地修改同一份状态。

它会直接把这种写法看成危险操作。

### 5.5 `ReadWriteLock<TState>` 决定隔离和冲突

这个锁不是一个普通 reader-writer lock。

它是按事务组、时间戳、优先级和访问计数来排的。

几个关键点：

- 同一个事务 re-enter 时，要校验读写计数有没有对上。
- 不同事务如果在同一 group 里没冲突，可以一起待着。
- 读事务和纯读事务可以并发，但只要有 writer 或 exclusive lock，就会收紧。
- 如果 `Priority` 更大，冲突会变成不可解。
- `LockTimeout` 和 `LockAcquireTimeout` 会把长时间拿不到锁的事务踢掉。

这就是 Orleans 事务里的隔离语义。

它不是标准数据库里的完整隔离级别抽象，而是一个按时间戳和锁组动态调整的执行模型。

我更愿意把它理解成“带冲突回滚能力的事务调度锁”，而不是纯粹的数据库锁。

### 5.6 `UseExclusiveLock` 是一个显式降冲突开关

它的用途很直接：

当读操作会频繁遇到升级冲突时，可以直接让读也拿独占锁，避免后面升级时再打架。

这说明 Orleans 事务不是只给你一个固定策略，而是给了一个实用的逃生口。

---

## 6. 日志和存储是怎么拼成恢复链的

关键文件：

- `src/Orleans.Transactions/State/TransactionQueue.cs`
- `src/Orleans.Transactions/State/StorageBatch.cs`
- `src/Orleans.Transactions/State/TransactionalStateStorageProviderWrapper.cs`
- `src/Orleans.Transactions/Abstractions/ITransactionalStateStorage.cs`

### 6.1 `ITransactionalStateStorage<T>` 是事务状态存储接口

它只有两个动作：

- `Load()`
- `Store(...)`

但是 `Store(...)` 里带了很多信息：

- 期望的 `ETag`
- metadata
- 一组要 prepare 的 pending states
- 要 commit 到哪一个 sequence
- 要 abort 到哪一个 sequence

这说明事务存储不是简单存一个 state，而是存一条状态日志。

### 6.2 `TransactionalStateStorageProviderWrapper<T>` 把普通 `IGrainStorage` 包成事务存储

如果你没有单独的事务存储工厂，`NamedTransactionalStateStorageFactory` 会退回去找 `IGrainStorage`，然后用 `TransactionalStateStorageProviderWrapper<T>` 包一层。

包装后的对象存的不是裸 state，而是：

- `CommittedState`
- `CommittedSequenceId`
- `Metadata`
- `PendingStates`

也就是说，事务状态在 storage 里长得像一个小型日志结构。

### 6.3 `StorageBatch<T>` 是真正的 batch 日志组装器

`StorageBatch<T>` 负责把这些事件拼成一次写入：

- `Prepare`
- `Read`
- `Cancel`
- `Confirm`
- `Commit`
- `Collect`

它还维护了：

- `confirmUpTo`
- `cancelAbove`
- `followUpActions`
- `storeConditions`

这里的意义很直白：

一轮事务不是每次都单条落盘，而是先聚成 batch，再统一写。

这既是为了性能，也是在为恢复做准备。

### 6.4 `TransactionQueue<T>` 是参与者侧的工作心脏

`TransactionQueue<T>` 这块是整个事务系统里最像“后台状态机”的部分。

它做的事包括：

- `NotifyOfRestore()`
- `Ready()`
- `EnqueueCommit(...)`
- `NotifyOfPrepared(...)`
- `NotifyOfPrepare(...)`
- `NotifyOfAbort(...)`
- `NotifyOfCancel(...)`
- `NotifyOfConfirm(...)`
- `NotifyOfPing(...)`

它维护三样东西：

- `commitQueue`
- `storageBatch`
- `RWLock`

启动时，它会：

1. `Load()` 现有存储。
2. 恢复 committed state。
3. 恢复 pending states。
4. 恢复 commit records。
5. 把未完成的远端事务重新挂回队列。

这就是 Orleans 事务的恢复逻辑。

它不是“失败就重新试一次请求”，而是“从存储日志里把事务状态重新接起来”。

### 6.5 `ETag` 是这里的乐观并发边界

`TransactionalStateStorageProviderWrapper.Store(...)` 会先检查 `expectedETag`。

如果对不上，直接抛 `ArgumentException`。

这很关键。

也就是说，事务系统内部虽然有自己的锁和调度，但最后落盘时还是会再过一层 optimistic concurrency 检查。

所以这套系统不是单层一致性，而是锁一致性加存储一致性双保险。

---

## 7. 失败语义和恢复语义为什么会这么多

关键文件：

- `src/Orleans.Transactions/TransactionalStatus.cs`
- `src/Orleans.Transactions/State/TransactionQueue.cs`
- `src/Orleans.Transactions/State/ActivationLifetime.cs`
- `src/Orleans.Transactions/State/TransactionalStateOptions.cs`
- `src/Orleans.Transactions/State/ReaderWriterLock.cs`

### 7.1 失败码多，不是为了炫技，是因为失败点很多

`TransactionalStatus` 把失败拆得很细：

- 提交阶段超时
- 参与者响应超时
- TM 响应超时
- 锁断了
- 锁验证失败
- 存储冲突
- 预设 abort
- 未知异常

这说明 Orleans 事务不是一个单线程本地临界区，它是跨 silo、跨 storage、跨恢复路径的系统。

所以失败不仅仅是“提交没成功”，而是“到底卡在哪一段没成功”。

### 7.2 `DefinitelyAborted()` 和 `ConvertToUserException()` 是两条不同的线

`DefinitelyAborted()` 只回答一个问题：

> 这个事务是不是确定死了？

`ConvertToUserException()` 才把 status 翻成用户能看到的异常。

这个拆法挺实际。

因为有些失败是确定 abort，有些只是 in doubt。

### 7.3 `TransactionQueue` 的恢复逻辑不是补一下就算了

一旦 `StorageWork()` 或锁状态出问题，`TransactionQueue` 会走 `AbortAndRestore(...)`：

- 先 abort 正在执行的事务
- 再 abort 队列里的事务
- 再清空 commit queue
- 再从 storage 重新 load

如果连续失败太多，或者 forced，就会触发 grain deactivation。

这个设计很现实，也很重。

它说明事务恢复不是轻量逻辑，而是整个参与者状态机的一部分。

### 7.4 `ActivationLifetime` 在这里扮演护栏

事务处理和 storage 批量写的时候，会通过 `BlockDeactivation()` 暂时挡住 deactivation。

`OnStop(...)` 时会先取消 `OnDeactivating`，然后等一小段时间让 pending deactivation lock 退出。

这就是为什么事务系统不会一边写日志一边被 activation 回收直接掐掉。

### 7.5 默认超时配置也说明它是“时间敏感系统”

`TransactionalStateOptions` 默认值很能说明问题：

- `LockTimeout` 8 秒
- `PrepareTimeout` 20 秒
- `LockAcquireTimeout` 10 秒
- `RemoteTransactionPingFrequency` 60 秒
- `ConfirmationRetryDelay` 30 秒
- `ConfirmationRetryLimit` 3
- `MaxLockGroupSize` 20

这不是一个“随便等着就能完成”的系统。

它默认就假设事务需要尽快收口，超时、重试、ping 和恢复都是常规路径，不是异常边角。

---

## 8. 还有一条旁支：`TransactionCommitter` / TOC

关键文件：

- `src/Orleans.Transactions/TOC/TransactionCommitter.cs`
- `src/Orleans.Transactions/TOC/TocTransactionQueue.cs`
- `src/Orleans.Transactions/TOC/TransactionCommitterFactory.cs`
- `src/Orleans.Transactions/Abstractions/TransactionCommitterAttribute.cs`
- `src/Orleans.Transactions/Hosting/TransactionCommitterAttributeMapper.cs`

这一条不是主干，但很值得提一下。

`TransactionCommitter<TService>` 把同一套事务机制挂到普通服务操作上了。

它和 `TransactionalState<T>` 很像：

- 都有 `ActivationLifetime`
- 都有 `TransactionQueue`
- 都有 `ParticipantId`
- 都在 `SetupState` 阶段注册资源工厂

不同的是，它不是围绕一份 grain state，而是围绕一个 service 的 commit operation。

`TocTransactionQueue<TService>` 还会在 `OnLocalCommit(...)` 里给 storage batch 加额外的 store precondition，把 service 自己的 `Commit(...)` 挂进去。

这说明 Orleans 事务并不只有“状态对象事务”这一条路，还有一条把事务逻辑包到服务操作上的平行入口。

这也是我觉得它复杂的一个原因。

同一套事务语义，横向分成了两个入口，底下又共享了很多相似的队列和恢复代码。

---

## 9. 这块最不够干净的地方

如果只从架构感受上说，我觉得这里至少有四个不够清爽的点。

### 9.1 事务有两条很像但不完全一样的入口

一条是 `TransactionAttribute` / `TransactionRequestBase` / `TransactionClient` / `TransactionAgent`。

另一条是 `TransactionalState<T>` / `TransactionCommitter<T>` / `TransactionQueue<T>` / `ReadWriteLock<T>`.

它们共享很多构件，但不是一套统一抽象。

### 9.2 调用上下文、锁、存储、恢复全都绑在一起了

`TransactionInfo`、`AsyncLocal`、grain extension、queue、batch、ETag、recover、timeout，这些概念不是分层放的，而是彼此嵌套。

这会让理解成本很高。

你很难只看一个文件就知道完整语义。

### 9.3 失败语义太细，收口点太多

状态码多，本来是好事。

但在 Orleans 里，这些状态码又会被转换成不同的用户异常、不同的 abort 路径、不同的 recover 行为。

这使得事务失败不太像“一个统一结果”，更像一组分叉的后续动作。

### 9.4 存储抽象有两层

一层是 `ITransactionalStateStorage<T>`。

另一层是退回到普通 `IGrainStorage` 再包一层 wrapper。

这很实用，但也说明事务存储不是单独干净的一层，还是靠“适配”撑起来的。

如果我要复刻，我大概率会把这两层拆得更直白一点。

---

## 10. 如果要自己复刻，我会怎么拆

如果目标是“保留事务能力，但架构收得更干净”，我会至少拆成四块。

### 10.1 事务上下文层

只负责：

- `TransactionInfo`
- `TransactionContext`
- `TransactionOption`
- ambient transaction 的传播和合并

### 10.2 协调层

只负责：

- `TransactionAgent`
- 2PC
- 超时和 abort
- participant 协商

### 10.3 参与者执行层

只负责：

- `TransactionalState<T>`
- `TransactionCommitter<T>`
- `TransactionQueue<T>`
- `ReadWriteLock<T>`

### 10.4 日志和恢复层

只负责：

- `StorageBatch<T>`
- `ITransactionalStateStorage<T>`
- `TransactionalStateStorageProviderWrapper<T>`
- `TransactionalStorageLoadResponse<T>`

这样拆之后，读代码的人就不会总被“上下文、锁、日志、恢复互相穿透”绕晕。

---

## 11. 推荐阅读顺序

如果你要从源码顺着吃透这一块，我建议按这个顺序看：

1. `src/Orleans.Transactions/TransactionAttribute.cs`
2. `src/Orleans.Transactions/TransactionContext.cs`
3. `src/Orleans.Transactions/DistributedTM/TransactionClient.cs`
4. `src/Orleans.Transactions/DistributedTM/TransactionAgent.cs`
5. `src/Orleans.Transactions/DistributedTM/TransactionInfo.cs`
6. `src/Orleans.Transactions/TransactionalStatus.cs`
7. `src/Orleans.Transactions/Abstractions/TransactionalStateAttribute.cs`
8. `src/Orleans.Transactions/Hosting/TransactionalStateAttributeMapper.cs`
9. `src/Orleans.Transactions/State/TransactionalStateFactory.cs`
10. `src/Orleans.Transactions/State/TransactionalState.cs`
11. `src/Orleans.Transactions/State/ReaderWriterLock.cs`
12. `src/Orleans.Transactions/State/TransactionQueue.cs`
13. `src/Orleans.Transactions/State/StorageBatch.cs`
14. `src/Orleans.Transactions/State/TransactionalStateStorageProviderWrapper.cs`
15. `src/Orleans.Transactions/TOC/TransactionCommitter.cs`

如果你把这 15 个文件顺完，基本就能把 Orleans 事务到底怎么工作的主体逻辑吃透。

---

## 12. 这一篇的落点

把这一篇收成一句话，就是：

Orleans 的事务系统不是一个“全局事务管理器”，而是一条从调用上下文，经过协调器，再落到参与者锁和存储日志的长链。

它能工作，是因为每一层都只管自己那一段：

- 调用层决定事务怎么进来。
- 协调层决定事务怎么收口。
- 参与者层决定事务怎么锁、怎么写、怎么恢复。
- 存储层决定事务怎么留下日志。

它不算干净，但很完整。

这也是它最像 Orleans 的地方。

