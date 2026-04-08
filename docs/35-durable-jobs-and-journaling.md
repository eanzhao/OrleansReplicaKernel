# DurableJobs 与 Journaling：两个边缘但很能说明 Orleans 风格的子系统（第三十五篇）

## 先说结论

`DurableJobs` 和 `Journaling` 都不是 Orleans 主干 runtime 的“核心转发链”，但它们很适合拿来研究 Orleans 的设计习惯。

`DurableJobs` 解决的是“把一个一次性任务可靠地放到未来某个时间执行，并且尽量做到至少一次”的问题。它本质上是一个按时间分片的调度系统，外面包着 shard 管理、membership 感知、重试、限流、慢启动和 grain 执行扩展。它更像应用层的调度基础设施，不是通用工作流引擎，也不是 reminder 的另一种写法。

`Journaling` 解决的是“把一组状态机和可变数据结构，用日志 + snapshot 的方式持久化和恢复”的问题。它本质上是一个小型的日志化 state machine 运行时，专门服务于 `IPersistentState<T>`、`DurableList`、`DurableQueue`、`DurableSet`、`DurableDictionary`、`DurableTaskCompletionSource` 这些东西。它和 Orleans 事务、流系统都有关联，但都不是一回事。

如果你要重建一版 Orleans，这两块最值得继承的是语义，不是现有的组织方式。语义可以保留，代码形状最好重切：调度、持久化、恢复、执行、重试这些责任，现在都压得有点重。

## 完整链路

### DurableJobs 的链路

1. 业务代码先通过 `ILocalDurableJobManager.ScheduleJobAsync` 提交一个 `ScheduleJobRequest`，里面带目标 grain、执行时间、名字和 metadata。
2. `LocalDurableJobManager` 先按时间把任务映射到某个 shard key，再去找现成的可写 shard。
3. 如果 shard 不存在，它会通过 `JobShardManager.CreateShardAsync` 创建新的 shard；如果 shard 已经存在，就直接往里面塞任务。
4. `JobShard.TryScheduleJobAsync` 负责把请求变成 `DurableJob`，再把“持久化加任务”这件事交给具体 shard 实现。
5. shard 内部先把任务放进内存队列 `InMemoryJobQueue`，再由具体存储实现把这次新增、删除或重试写出去。
6. `LocalDurableJobManager` 还会持续看集群 membership 和定时检查，把空闲 shard、孤儿 shard、死节点上的 shard 重新分配给本 silo。
7. shard 到点后，`ShardExecutor.RunShardAsync` 会遍历 `ConsumeDurableJobsAsync()`，控制并发、检查过载、处理慢启动，然后把任务派给目标 grain。
8. 任务执行不是直接调用户接口，而是通过 `IDurableJobReceiverExtension.HandleDurableJobAsync` 这层 grain extension 进入。
9. extension 再检查 grain 是否实现 `IDurableJobHandler`，如果实现了，就调用 `ExecuteJobAsync`。
10. 执行结果由 `DurableJobRunResult` 决定后续动作：完成就移除，失败就按 `ShouldRetry` 重新安排，`PollAfter` 就在本地循环里继续轮询。

### Journaling 的链路

1. silo 启动时先调用 `AddStateMachineStorage()`，注册 `IStateMachineStorageProvider`、`StateMachineManager`，以及一组 keyed 的 durable 类型。
2. grain 实例里拿到 `IStateMachineManager` 之后，会在生命周期里把自己挂进去，或者通过 `DurableGrain.GetOrCreateStateMachine()` 显式创建 durable 对象。
3. 每个 durable 对象都实现 `IDurableStateMachine`，注册时会绑定一个稳定名字，再由 manager 给它分配内部 `StateMachineId`。
4. `StateMachineManager` 先从 `IStateMachineStorage.ReadAsync()` 读回所有 `LogExtent`，按 `StateMachineId` 把每条 entry 分发给对应状态机 `Apply()`。
5. 恢复完成后，状态机进入正常运行态；后续的修改会通过 `IStateMachineLogWriter`、`StateMachineStorageWriter` 和 `LogExtentBuilder` 写成日志条目。
6. 如果存储请求 compaction，manager 会把当前状态写成 snapshot，再通过 `ReplaceAsync()` 覆盖旧日志；否则就用 `AppendAsync()` 继续追加。
7. 写完之后，manager 会回调每个状态机的 `OnWriteCompleted()`，让它们更新版本号、释放脏标记或者完成挂起的 task。
8. 退出或长期未注册的状态机不会立刻删掉，manager 会先把它们放进 `RetiredStateMachineVessel`，留一个宽限期，等 compaction 时再真正清理。

## 关键组件

### DurableJobs 这一组

`LocalDurableJobManager` 是调度中枢。它负责找 shard、创建 shard、跟 membership 互动、做 shard activation、执行清理和重试串联。它不是简单的 helper，而是一个真正的系统目标 `SystemTarget`。

`JobShardManager` 是 shard 归属的抽象。`InMemoryJobShardManager` 只是测试/演示实现，但它已经把“孤儿 shard 认领”“死节点接管”“poison shard”这些现实问题想进去了。

`JobShard` 是 shard 的基本行为合同。它把“任务集合”“时间窗口”“持久化新增/删除/重试”绑在一起，`InMemoryJobQueue` 负责按 due time 组织任务。

`ShardExecutor` 是执行器。它的职责很杂：限流、慢启动、过载等待、轮询执行、失败重试、成功删除。这个类很能说明 DurableJobs 的性格，逻辑上是一个独立调度器，代码上已经很接近一个小型运行时了。

`IDurableJobReceiverExtension` 是执行入口。它用 `[AlwaysInterleave]` 让 job 执行和 grain 自己的 turn 不互相卡死，再通过 `GrainInstance is IDurableJobHandler` 直接找到用户代码。

`DurableJobsOptions` 把这块能力暴露出来的运行参数都收在一起了，比如 shard duration、activation buffer、并发上限、慢启动、重试策略、adopt budget。它说明这套东西已经不是“简单定时器”，而是一套可调度系统。

### Journaling 这一组

`StateMachineManager` 是最核心的东西。它不是单纯的持久化 helper，而是一个带恢复、写日志、写 snapshot、退役管理、内部元数据管理的小运行时。

`IDurableStateMachine` 是协议核心。它把恢复、应用、写出、写后回调、复制这些职责都收进来了，状态机的生命周期和持久化协议是绑在一起的。

`IStateMachineStorage` / `IStateMachineStorageProvider` 是真正的 storage 入口。前者定义读、追加、覆盖、删除，后者按 grain 创建存储实例。这里的 storage 是“状态机日志存储”，不是 Orleans 主消息存储。

`LogExtent` 和 `LogExtentBuilder` 是日志格式本身。它们把条目按 `StateMachineId + payload` 的方式封装起来，再用可追加、可快照的结构组织 segment。

`DurableState<T>` 是 `IPersistentState<T>` 的日志化实现。它把“状态值 + 版本号”写进同一个状态机里，比较接近 Orleans 现有 persistent state 的语义，只是实现路径换成了 journaling。

`DurableList<T>`、`DurableQueue<T>`、`DurableSet<T>`、`DurableDictionary<K,V>`、`DurableValue<T>`、`DurableTaskCompletionSource<T>` 是最能看出这套系统定位的几个类型。它们不是 stream，也不是 transaction 参与者，而是一组“带日志的本地数据结构”。

`DurableGrain` 是使用侧的薄封装。它帮 grain 直接拿到 `IStateMachineManager`，再提供 `GetOrCreateStateMachine()` 和 `WriteStateAsync()` 这两个入口，让用户不用自己手写注册和生命周期挂钩。

## 它们和 storage、transactions、streaming 的关系

`DurableJobs` 和 `Journaling` 都离 storage 很近，但近的方式不一样。

`DurableJobs` 的 storage 关注点是“任务、分片、归属、恢复”。它要保证 shard 和 job 的持久性，让任务跨 silo、跨重启还能继续跑。真正的持久化后端是通过具体 storage provider 接上去的，`InMemoryJobShardManager` 只是一个演示级实现。

`Journaling` 的 storage 关注点是“日志段、snapshot、恢复回放”。它直接把 storage 当成日志介质来用，`AppendAsync`、`ReplaceAsync`、`DeleteAsync` 都是 state machine 级别的操作。

它们和 Orleans transactions 不是一条线。事务解决的是“多 grain 或多资源的一致性提交”，这两套都不是在做分布式事务协调。`Journaling` 只是把单个 grain 的多个状态机写得更稳；`DurableJobs` 只是把一个未来任务调度得更可靠。它们都没有提供跨 grain 的原子提交。

它们和 streaming 也不是一条线。streaming 解决的是“事件流的生产、订阅、消费位点、回压和分发”。`DurableJobs` 不是 pub/sub，也不是消费组；它是一次性任务调度。`Journaling` 里的 `DurableQueue<T>` 只是一个持久化队列，不是 Orleans stream 的消费者队列。

如果非要一句话概括边界：

`DurableJobs` 更像“带持久化的未来执行器”。

`Journaling` 更像“带日志的状态机和容器运行时”。

它们都不是主消息链，也都不是完整一致性系统。

## 架构上不够干净的地方

`DurableJobs` 最大的问题，是它把调度、分片、执行、重试、过载控制、membership 处理全揉进了一条链里。`LocalDurableJobManager` 和 `ShardExecutor` 都太像“总控对象”了，后面真要重建，最好把“任务分配”“任务执行”“失败策略”“重试策略”“集群接管”拆得更开。

`DurableJobReceiverExtension` 也有点隐式。它不是通过一个显式的 grain contract 调用用户逻辑，而是直接看 `GrainInstance` 是否实现 `IDurableJobHandler`。这能跑，但抽象边界不算漂亮。

`Journaling` 的问题更像“自己长成了一个小 runtime”。`StateMachineManager` 里同时有恢复、写日志、snapshot、退役、内部元数据、生命周期接入、工作队列，这种集中式写法很强，但也很容易让阅读成本变高。

`StateMachineManagerState`、`StateMachinesRetirementTracker`、`RetiredStateMachineVessel` 这些内部对象说明它已经不是一个普通持久化库，而是一个“状态机上的状态机”。这很实用，但对外部读者来说不够直观。

`DurableState<T>`、`DurableValue<T>`、`DurableList<T>` 等类型的写法也不完全统一。有些类型是修改时立刻落日志，有些是攒脏标记后由 manager 统一写，有些又直接把 snapshot 当主路径。语义上可以理解，代码风格上不够整齐。

还有一点比较明显：`Journaling` 的抽象层很多，但很多地方还是强依赖 keyed DI、内部约定和特定生命周期。它能把东西装进去，但不像一个从头到尾都很“薄”的基础库。

## 如果我要重建，应该怎么取舍

我会保留这两块的“语义核心”，但不会原样继承它们的代码形状。

`DurableJobs` 该保留的是：一次性任务、时间分片、至少一次、可取消、可重试、可接管。该重写的是：分片管理、执行器、接管策略、重试策略的边界。

`Journaling` 该保留的是：日志化状态机、快照、恢复、可组合的持久化数据结构。该重写的是：状态机注册、内部元数据、持久化写入路径和 API 的统一性。

如果目标是“重新构建一个更干净的 Orleans”，这两块都可以留下，但更适合被拆进更明确的子层，而不是像现在这样各自长成一套半独立运行时。

## 推荐阅读顺序

1. 先看 `src/Orleans.DurableJobs/README.md`，先把 Durable Jobs 的业务语义看明白。
2. 再看 `src/Orleans.DurableJobs/Hosting/DurableJobsExtensions.cs` 和 `src/Orleans.DurableJobs/Hosting/DurableJobsOptions.cs`，搞清楚它怎么挂进 silo。
3. 然后看 `src/Orleans.DurableJobs/LocalDurableJobManager.cs`、`src/Orleans.DurableJobs/JobShard.cs`、`src/Orleans.DurableJobs/ShardExecutor.cs`，把整条调度链串起来。
4. 接着看 `src/Orleans.Journaling/HostingExtensions.cs` 和 `src/Orleans.Journaling/StateMachineManager.cs`，理解 journaling 的总控逻辑。
5. 再看 `src/Orleans.Journaling/IDurableStateMachine.cs`、`src/Orleans.Journaling/LogExtent.cs`、`src/Orleans.Journaling/LogExtentBuilder.cs`，理解日志格式和协议。
6. 最后按类型看 `src/Orleans.Journaling/DurableState.cs`、`src/Orleans.Journaling/DurableList.cs`、`src/Orleans.Journaling/DurableQueue.cs`、`src/Orleans.Journaling/DurableDictionary.cs`、`src/Orleans.Journaling/DurableSet.cs`、`src/Orleans.Journaling/DurableValue.cs`、`src/Orleans.Journaling/DurableTaskCompletionSource.cs`、`src/Orleans.Journaling/DurableGrain.cs`，你会更容易看出这套系统到底在为谁服务。

