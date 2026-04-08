# 调度与并发模型：WorkItemGroup、ActivationTaskScheduler、reentrancy、interleaving 是怎么串起来的（第六篇）

这一篇专门讲 Orleans 的“单线程看起来很简单，实际规则一大堆”的那部分。

前四篇讲完以后，我们已经知道：

- `GetGrain` 拿到的是引用，不是对象
- 调用方法时会变成 `IInvokable`
- 消息会走序列化和网络
- Grain 引用、类型名、消息头都有自己的一套规则

这篇再往下走一步，直接看 grain 在本地到底是怎么被调度的。

先把结论说清楚：

- Orleans 的 grain 调度核心不是 `TaskScheduler` 本身，而是 `WorkItemGroup`。
- `ActivationTaskScheduler` 只是 `WorkItemGroup` 对外暴露出来的 `TaskScheduler` 包装。
- 默认情况下，一个 activation 同一时间只跑一个 turn，消息按队列顺序处理。
- 能不能“插队”，不是看线程池心情，而是看消息标记、grain 属性、当前阻塞请求和 call-chain reentrancy。
- `reentrancy` 不是“随便并发”，而是 Orleans 允许某些调用在同一个 activation 里重入。
- `interleaving` 比 `reentrancy` 更细，允许某些消息在已有请求没结束时进入。
- `call-chain reentrancy` 不是全局放开，而是靠 `RequestContext.#CCR` 只对同一条调用链开口子。

如果你先把这几条分清楚，后面读 `ActivationData` 就不会一直打架。

---

## 1. 先把整条链压成一张图

```text
activation 创建
  -> ActivationDataActivatorProvider
  -> ActivationData
  -> WorkItemGroup
  -> ActivationTaskScheduler

消息进入 activation
  -> ReceiveMessage
  -> ReceiveRequest / ReceiveResponse
  -> _waitingRequests
  -> _workSignal
  -> ProcessPendingRequests
  -> MayInvokeRequest
  -> RecordRunning
  -> InvokeIncomingRequest
  -> RuntimeClient.Invoke
  -> 具体 grain 方法执行
  -> OnCompletedRequest
  -> _workSignal.Signal()

线程调度
  -> WorkItemGroup.EnqueueTask
  -> ScheduleExecution(this)
  -> WorkItemGroup.Execute
  -> ActivationTaskScheduler.RunTaskFromWorkItemGroup
  -> TaskScheduler.TryExecuteTaskInline
```

这张图里最关键的一点是：

Orleans 不是把消息直接丢给线程池，然后靠锁去保平安。

它先把消息变成 grain 级别的 work item，再按 activation 自己的调度器跑。

这就是它能把“单线程语义”做得比较稳的原因。

---

## 2. activation 创建时，调度器是怎么挂上去的

关键文件：

- `src/Orleans.Runtime/Activation/ActivationDataActivatorProvider.cs`
- `src/Orleans.Runtime/Catalog/ActivationData.cs`
- `src/Orleans.Runtime/Scheduler/WorkItemGroup.cs`
- `src/Orleans.Runtime/Scheduler/ActivationTaskScheduler.cs`

### 2.1 `ActivationDataActivatorProvider` 先把上下文装起来

`ActivationDataActivatorProvider` 在创建 grain activation 时，会先构造：

- `ActivationData`
- `WorkItemGroup`
- `ActivationTaskScheduler`

其中最关键的是这一层关系：

```text
ActivationData
  -> owns WorkItemGroup
  -> WorkItemGroup owns ActivationTaskScheduler
```

也就是说，`TaskScheduler` 并不是独立资源，它只是某个 activation 的调度视图。

### 2.2 `ActivationData.Start(...)` 是在自己的 scheduler 上启动的

`ActivationDataActivatorProvider` 最后会这样启动 activation：

```csharp
Task.Factory.StartNew(
    _startActivation,
    context,
    CancellationToken.None,
    TaskCreationOptions.DenyChildAttach,
    context.ActivationTaskScheduler);
```

这件事很重要。

activation 的 `Start` 不是随便在哪个线程上跑，而是明确跑在它自己的 `ActivationTaskScheduler` 上。

`ActivationData.Start(...)` 里还有一个断言：

```csharp
Debug.Assert(Equals(ActivationTaskScheduler, TaskScheduler.Current));
```

这不是装饰。

它是在告诉你：这个对象内部很多逻辑默认都建立在“我正在正确的 grain scheduler 上运行”这个前提下。

### 2.3 `WorkItemGroup` 是真正的队列

`WorkItemGroup` 维护了自己的状态机：

- `Waiting`
- `Runnable`
- `Running`

它内部保存的是一个 `Queue<Task>`，不是消息，不是请求对象。

所以在 Orleans 这里，进入调度器的最终形态已经是 `Task` 了。

这个设计有好处也有代价：

- 好处是直接复用 .NET `Task` 和 `TaskScheduler` 的执行模型
- 代价是 Orleans 自己要在上层再包一层消息语义，心智模型会变厚

---

## 3. 消息是怎么从队列里进到 turn 的

关键文件：

- `src/Orleans.Runtime/Catalog/ActivationData.cs`
- `src/Orleans.Runtime/Scheduler/SchedulerExtensions.cs`
- `src/Orleans.Runtime/Scheduler/TaskSchedulerUtils.cs`
- `src/Orleans.Runtime/Core/InsideRuntimeClient.cs`

### 3.1 `ReceiveMessage(...)` 先分响应和请求

消息进 activation 后，先走：

- `ReceiveResponse(...)`
- `ReceiveRequest(...)`

响应很简单，直接交给 `RuntimeClient.ReceiveResponse(...)`。

请求才进入真正的调度链。

### 3.2 请求先排队，再由 message pump 消费

`ReceiveRequest(...)` 做的事很直接：

1. 检查 overload
2. 把消息放进 `_waitingRequests`
3. ` _workSignal.Signal()`

然后 message loop 会跑 `ProcessPendingRequests()`。

它不是一收到消息就立刻执行，而是先放队列，再由自己的泵去挑能执行的请求。

这是 Orleans 单线程语义的核心。

### 3.3 真正执行前先过 `MayInvokeRequest(...)`

`ProcessPendingRequests()` 会按队列顺序看每条消息，先问：

```csharp
MayInvokeRequest(message)
```

这一步就是“能不能插队”的总闸门。

如果不能执行，它不会丢消息，也不会阻塞整个泵，而是先跳到下一条消息去试。

这意味着：

- 队列顺序在
- 但执行顺序不一定完全等于入队顺序

前提是这条消息本身有资格被插到前面。

### 3.4 通过后会先记录 running，再真正调用

一旦通过检查，代码会：

1. 从 `_waitingRequests` 移除消息
2. `RecordRunning(message, message.IsAlwaysInterleave)`
3. 调 `InvokeIncomingRequest(message)`

`RecordRunning(...)` 里会把消息放进 `_runningRequests`。

如果不是 reentrant activation，或者消息本身不是 interleavable，它还会把 `_blockingRequest` 设成当前消息。

这个 `_blockingRequest` 很关键。

它就是 Orleans 判断“当前这个 activation 是不是已经被一个会挡住后续消息的请求占住了”的依据。

### 3.5 真正进业务代码的是 `RuntimeClient.Invoke(...)`

`InvokeIncomingRequest(...)` 最后会走：

```csharp
_shared.InternalRuntime.RuntimeClient.Invoke(this, message);
```

这一步才会把 `Message.BodyObject` 里的 `IInvokable` 真正执行掉。

所以流程可以简化成：

```text
Message -> 队列 -> 许可判断 -> running 集合 -> RuntimeClient.Invoke -> grain 方法
```

---

## 4. `WorkItemGroup` 和 `ActivationTaskScheduler` 各自负责什么

关键文件：

- `src/Orleans.Runtime/Scheduler/WorkItemGroup.cs`
- `src/Orleans.Runtime/Scheduler/ActivationTaskScheduler.cs`

### 4.1 `WorkItemGroup` 管状态，`ActivationTaskScheduler` 管 Task 接口

这俩不是一回事。

`WorkItemGroup` 负责：

- 入队
- 出队
- 状态切换
- 执行节奏
- 队列长度警告
- turn 长时间运行警告

`ActivationTaskScheduler` 负责：

- 把 `Task` 丢回 `WorkItemGroup`
- 决定哪些 task 可以 inline 执行
- 把自己包装成 .NET 能理解的 `TaskScheduler`

所以它们的边界大概是：

```text
WorkItemGroup = grain 级运行队列
ActivationTaskScheduler = 这个队列的 TaskScheduler 外壳
```

### 4.2 `WorkItemGroup.Execute()` 一次会跑多个微 turn

`Execute()` 的注释已经写得很直白：

> Execute one or more turns for this activation.

它不是严格一个消息一个线程切片，而是会在时间片范围内尽量多跑一些 work item。

这和 `SchedulingOptions.ActivationSchedulingQuantum` 有关。

如果这个量子时间没到，它会继续 drain 队列；如果到了，就把线程还回去。

这也是 Orleans 在吞吐和公平性之间做的一个折中。

### 4.3 `ActivationTaskScheduler.TryExecuteTaskInline(...)` 不是随便 inline

inline 的条件很窄：

- 这个 task 之前没有排过队
- 当前执行上下文就是这个 grain context

也就是说，只有在同一个 grain 上下文里、并且任务本来就没被排进队列时，才允许 inline。

这能减少一层调度开销，但不会把调度语义弄乱。

### 4.4 `QueueAction(...)` 还会压掉 execution context

`TaskSchedulerUtils.QueueAction(...)` 在入队时会用：

```csharp
using var suppressExecutionContext = new ExecutionContextSuppressor();
```

这说明 Orleans 对 scheduler 这层的态度很明确：

不要把调用方的 execution context 随便带到 grain 调度线程里。

这点对隔离很重要，但也让调度代码更显式、更“自己管自己”。

---

## 5. `reentrancy`、`interleaving`、`call-chain reentrancy` 分别是什么

这是最容易混的地方。

### 5.1 `reentrancy` 是 grain 级的策略

关键文件：

- `src/Orleans.Core.Abstractions/Concurrency/GrainAttributeConcurrency.cs`

`[Reentrant]` 是作用在 grain implementation class 上的属性。

它的意思不是“这个 grain 可以随便并发”，而是：

> 允许请求在同一个 activation 内重入，提升并发度。

这已经比纯串行松了，但仍然是 Orleans 的受控并发。

### 5.2 `interleaving` 是 message 级的许可

`Message` 上有两个很关键的标记：

- `IsReadOnly`
- `IsAlwaysInterleave`

这两个标记不是装饰，它们直接影响 `MayInvokeRequest(...)`。

`ReadOnly` 的意思是这条调用不修改状态，所以可以和其他 `ReadOnly` 请求并行插入。

`AlwaysInterleave` 的意思更强，连写请求也可以插。

对应的生成端来源在：

- `src/Orleans.Core.Abstractions/CodeGeneration/InvokeMethodOptions.cs`
- `src/Orleans.Core.Abstractions/Concurrency/GrainAttributeConcurrency.cs`

也就是说，方法级 attribute 最后会变成 message flag，再进入 scheduler 判断。

### 5.3 `call-chain reentrancy` 是一条调用链自己的开口子

关键文件：

- `src/Orleans.Core.Abstractions/Runtime/RequestContext.cs`
- `src/Orleans.Core.Abstractions/Core/Internal/ICallChainReentrantGrainContext.cs`
- `src/Orleans.Runtime/Catalog/ActivationData.cs`
- `src/Orleans.Core/Runtime/RequestContextExtensions.cs`

这个机制的核心字段是：

```csharp
RequestContext.CALL_CHAIN_REENTRANCY_HEADER // "#CCR"
```

`RequestContext.AllowCallChainReentrancy()` 会生成或复用一个 reentrancy id。

在这个作用域里发出去的请求，会带上同一个 `#CCR`。

收到消息以后，`ActivationData.MayInvokeRequest(...)` 会检查：

```csharp
incoming.GetReentrancyId()
```

如果这个 id 在当前 activation 的 `ReentrantRequestTracker` 里已经激活了，就允许它重入。

这和 `[Reentrant]` 不一样。

`[Reentrant]` 是全局策略，说明这个 grain class 整体允许重入。

`call-chain reentrancy` 是局部策略，说明这条调用链在一个受控区间内可以互相穿透。

这就是 Orleans 的一个很典型的“边界不那么清爽但很实用”的设计。

### 5.4 `MayInterleaveAttribute` 是另一个更细的钩子

`[MayInterleave]` 会把一个回调方法名写进 grain properties：

- `MayInterleavePredicate`

`ActivationData.MayInvokeRequest(...)` 在前面的规则都没放行时，会继续问这个 predicate：

```csharp
canInterleave.MayInterleave(GrainInstance, incoming)
    || canInterleave.MayInterleave(GrainInstance, _blockingRequest)
```

这很有意思。

它不是只看“当前 incoming 能不能插”，还会看“已经挡在前面的 blocking request 能不能被插”。

也就是说，interleaving 规则在 Orleans 里不是单边判断，而是带着当前阻塞请求一起判断。

---

## 6. `MayInvokeRequest(...)` 的判断顺序

这一段最好单独拎出来，因为它就是 Orleans 调度策略的中心。

判断顺序大概是这样：

1. 如果 activation 当前没有在执行，直接放行
2. 如果 incoming 是 `AlwaysInterleave`，放行
3. 如果当前没有 blocking request，放行
4. 如果 blocking request 和 incoming 都是 `ReadOnly`，放行
5. 如果 `#CCR` 命中当前 activation 的 reentrant section，放行
6. 如果 grain 配了 `MayInterleave` predicate，再问一次
7. 其他情况，不放行

这套顺序说明一件事：

Orleans 的调度不是靠单一策略，而是多层条件叠在一起。

所以你看代码时会觉得它“判断很多”，其实不是多余，而是在兼容不同语义：

- 普通串行请求
- 只读请求
- 强制插队请求
- 调用链级 reentrancy
- 用户自定义 interleaving

---

## 7. 为什么说 Orleans 的调度语义没有那么干净

这部分我直说。

### 7.1 规则散在好几层

能不能并发，不是在一个地方决定的，而是散在这些地方：

- `Message.IsReadOnly`
- `Message.IsAlwaysInterleave`
- `[Reentrant]`
- `[AlwaysInterleave]`
- `[MayInterleave]`
- `RequestContext.#CCR`
- `ActivationData._blockingRequest`
- `ActivationData.MayInvokeRequest(...)`

这会让调度策略很强，但也很难一眼看懂。

### 7.2 `WorkItemGroup` 和 `ActivationTaskScheduler` 这层分裂感很强

一个管状态，一个管 `TaskScheduler` 外壳。

这种拆法在工程上是合理的，但从架构感上说不够“一个概念一个对象”。

你要追一个 turn 是怎么跑起来的，必须两边都看。

### 7.3 `call-chain reentrancy` 是通过 `RequestContext` 这种隐式通道传的

这点最麻烦。

它不是显式参数，也不是显式请求头类型，而是靠一个字符串 key `#CCR` 藏在 request context 里。

优点是兼容性强。

缺点是可读性差，调试时也不直观。

### 7.4 `MayInvokeRequest` 里已经写了“这套逻辑只适用于 non-reentrant activations”

源码里多次能看到类似注释：

- long request detection 只对 non-reentrant activations 有效
- 某些忙碌检测只对 non-reentrant activations 有效

这说明 Orleans 这套调度逻辑不是完全统一的，很多分支其实是在往旧语义上补丁。

---

## 8. 顺手看一眼 timer 和 local object，调度边界会更清楚

### 8.1 timer 也是进 scheduler 的

`IGrainBase.RegisterGrainTimer(...)` 的文档已经写得很明白：

- timer callback 不会和自己并发执行
- `Interleave=true` 时可以和其他 grain method / timer 交错
- `Interleave=false` 时遵守 grain 的 reentrancy 规则

也就是说，timer 不是调度旁路，而是调度系统的一部分。

### 8.2 local object / observer 有自己的特殊路径

`InvokableObjectManager.LocalObjectData` 里对 `IsAlwaysInterleave` 的 message 直接单独处理，不走普通队列。

这说明 Orleans 在一些特殊对象上，已经不满足于一套统一 scheduler 了。

这不是坏事，但它进一步说明：

调度语义在 Orleans 里不是一张平整的纸，而是很多层特例叠出来的。

---

## 9. 如果你要自己复刻，我会怎么拆

如果目标是“做一版更干净的 Orleans 风格 runtime”，这一块我建议至少拆成三层。

### 9.1 调度核心层

只负责：

- queue
- turn
- runnable / running / waiting 状态
- 线程切换

不要把 reentrancy 策略和消息语义塞进来。

### 9.2 并发策略层

只负责：

- read-only
- always interleave
- reentrant grain
- custom interleave predicate
- call-chain reentrancy

这一层可以输出一个很简单的决策结果：

```text
CanRunNow / MustWait / CanBypassBlockingTurn
```

### 9.3 调用链上下文层

只负责：

- `RequestContext`
- `#CCR`
- tracing / activity
- cancellation / propagation

不要把这些东西和 scheduler 的队列状态搅在一起。

这样拆开以后，调度逻辑会更容易测，也更容易解释。

---

## 10. 这一篇最该反复看的源码文件

建议按这个顺序读：

1. `src/Orleans.Runtime/Activation/ActivationDataActivatorProvider.cs`
2. `src/Orleans.Runtime/Catalog/ActivationData.cs`
3. `src/Orleans.Runtime/Scheduler/WorkItemGroup.cs`
4. `src/Orleans.Runtime/Scheduler/ActivationTaskScheduler.cs`
5. `src/Orleans.Runtime/Scheduler/SchedulerExtensions.cs`
6. `src/Orleans.Runtime/Scheduler/TaskSchedulerUtils.cs`
7. `src/Orleans.Core.Abstractions/Concurrency/GrainAttributeConcurrency.cs`
8. `src/Orleans.Core.Abstractions/Runtime/RequestContext.cs`
9. `src/Orleans.Core.Abstractions/Core/Internal/ICallChainReentrantGrainContext.cs`
10. `src/Orleans.Core/Runtime/RequestContextExtensions.cs`
11. `src/Orleans.Core/Messaging/Message.cs`
12. `src/Orleans.Core.Abstractions/CodeGeneration/InvokeMethodOptions.cs`
13. `src/Orleans.Core.Abstractions/Core/IGrainContext.cs`

把这几份文件顺完以后，再回去看 `ActivationData.ProcessPendingRequests()`，就会明白 Orleans 这个所谓“单线程 grain”到底是怎么保持住的。

---

## 11. 这一篇的落点

把这一篇压成一句话，就是：

Orleans 的调度不是“一个 activation 一个锁”这么粗暴，而是一套围绕 `WorkItemGroup` 建起来的 turn-based 调度系统，外面再套上 reentrancy、interleaving、call-chain reentrancy 这些策略，最后才形成你看到的 grain 并发语义。

它的好处是表达力很强，很多业务场景都能兜住。

它的坏处也很明显：规则太多，分散在消息、属性、上下文和 scheduler 几层里，读源码时很难一下子把全貌拼起来。

下一篇如果继续写，我建议接这两个方向里的一个：

- `ActivationData` 为什么会成为 Orleans 复杂度中心
- `GrainDirectory`、`PlacementService`、地址缓存是怎么把一条消息送到正确 activation 的
