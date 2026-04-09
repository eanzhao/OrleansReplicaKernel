# 69. Request-chain reentrancy 与 self-call 死锁规避

这一篇接在 `68` 后面，继续把调度层往 Orleans 的 actor 语义推进。

上一轮我们做的是“方法级 interleaving hint”：

- 某些方法可以并发重叠执行
- 其他方法还是默认独占

但这还解决不了一个更经典的问题：

- activation 正在处理一个独占 turn
- 这个 turn 里又通过 grain reference 发起了下一跳调用
- 下一跳最终又回到了同一个 activation

如果 runtime 只是“看到 activation 忙，就老老实实排队”，那这条链就会卡成“自己等自己”。

所以这一轮补的是更关键的一刀：

- `request-chain reentrancy`

## 1. 这轮补的到底是什么

这次不是做“整个 grain 都可重入”，也不是直接照搬 Orleans 全套 reentrancy 模型。

我补的是一条更窄、但非常关键的边界：

- 每个顶层请求都会有一个 `request chain id`
- grain 在 turn 里再发起 nested invocation 时，会继承这个 chain id
- 如果这个 nested invocation 最终又打回当前 activation
- 而且 chain id 和当前正在执行的独占 turn 属于同一条逻辑调用链
- 那 scheduler 允许它重入当前独占 turn

这件事解决的是：

- `self-call`
- `A -> B -> A`
- `A -> observer -> A`

这一类“同链路回跳”最容易出现的死锁。

## 2. 这轮改了哪几层

这一轮真正接上的链路是：

1. `ActivationExecutionContext`
2. `InProcessRuntime`
3. `InvocationMessage`
4. `ActivationScheduler`

### 2.1 ActivationExecutionContext

以前这里只存：

- `CurrentRuntime`

现在会一起存：

- `CurrentRuntime`
- `CurrentGrainId`
- `CurrentRequestChainId`

这样 grain 方法体里如果要再发起 grain 调用，就能自然继承当前上下文，而不是重新开一条完全陌生的新链。

### 2.2 InProcessRuntime

以前 runtime 每次 `InvokeAsync(...)` 都只生成新的 `RequestId`。

现在变成：

- 顶层调用：`RequestChainId = RequestId`
- activation 内部发出的 nested 调用：沿用 `ActivationExecutionContext.CurrentRequestChainId`

也就是说：

- 每次尝试仍然有自己的 `RequestId`
- 但同一条逻辑链共享同一个 `RequestChainId`

这个区分很重要，因为 dedupe / retry 还是看 request，自调用重入则看 chain。

### 2.3 InvocationMessage

这轮给 `InvocationMessage` 补了：

- `RequestChainId`

从这里开始，request-chain 信息才真正能从 source node 一路传到 target activation。

### 2.4 ActivationScheduler

这轮 scheduler 里多了一条新规则：

- 如果当前有独占 turn 在跑
- 新来的 turn 属于同一个 `RequestChainId`
- 那它可以重入当前独占 turn

但这不代表所有东西都能随便插队。

当前保留的规则是：

- 不同 chain 之间还是要守 barrier
- 同链回跳才允许重入
- 方法级 interleaving hint 仍然生效
- `request-chain reentrancy` 和 `interleaving hint` 是两条并行的判定线，不是互相替代

## 3. 这不等于“完整 reentrant grain”

这里必须讲清楚，不然很容易误判当前实现进度。

这轮已经做到的是：

- 同一条逻辑调用链，允许重入当前 activation

这轮还没做到的是：

- grain 级 `[Reentrant]`
- 更复杂的 call-chain depth 控制
- observer / callback / timer / reminder 的分类调度
- 更精细的 request ordering 规则
- 更完整的 deadlock prevention 策略
- Orleans 里那些已经被长期打磨过的边角行为

所以更准确的说法是：

- 现在 `OrleansReplicaKernel` 已经有了“call-chain reentrancy 的第一块内核”
- 但还没有“完整 Orleans reentrancy 模型”

## 4. demo 里怎么验证

这轮我在 `EchoGrain` 里加了一条很直接的演示：

- `ReentrantSelfCallAsync(int remaining)`

它做的事情很简单：

1. 当前 activation 先处理一次方法
2. 如果 `remaining > 0`
3. 就通过当前 activation 的 runtime 和当前 grain id，再调自己一次
4. 新调用沿用同一个 `RequestChainId`
5. scheduler 识别出“这是同链回跳”，允许它重入

如果没有这轮的 request-chain reentrancy，这段代码会直接卡死在“外层 turn 等内层 turn，内层 turn 又排在外层后面”。

现在这条链会正常跑完，而且计数会一路变成：

- `1 -> 2 -> 3`

## 5. 这轮测试覆盖了什么

这轮我补了两层测试。

### 5.1 Scheduler 级

`ActivationSchedulerTests` 里新增了两条：

- 同 chain 的 exclusive turn 可以重入
- 不同 chain 的 exclusive turn 不能误重入

这层测试只看 scheduler 本身，验证的是最底层 barrier 语义。

### 5.2 Runtime / Host 级

`InvocationResponseSemanticsTests` 里新增了：

- `SameCallChain_CanReenterExclusiveActivation`

这条测试证明：

- 同一个 grain 在一条逻辑调用链里回调自己
- 当前 runtime 已经能跑通
- 不会因为 activation 默认独占而死锁

## 6. 这轮真正解决了什么问题

这轮的真实价值不是“多了一个新 demo 方法”，而是 runtime 现在终于有能力区分：

- 一个完全无关的新请求
- 和一条正在执行的调用链打回来的 reentrant 请求

这两者在 actor runtime 里必须分开处理，否则你永远只能得到一种过于粗暴的行为：

- 要么全部串行，结果死锁
- 要么全部放开，结果语义变脏

现在 `OrleansReplicaKernel` 终于有了第三种可能：

- 默认守独占
- 只有同链回跳才允许重入

这正是后面继续补 Orleans 风格 reentrancy 所必需的台阶。

## 7. 后面最自然该接什么

这一轮做完后，下一步最自然的不是继续狂加 demo，而是把这条线往更真实的 Orleans 语义推进：

- observer callback scheduling
- call-chain reentrancy 与方法级 interleaving 的组合规则
- 更明确的 reentrancy metadata
- timer / reminder / system target 的调度分类

所以 `69` 的定位可以概括成一句话：

它不是把 Orleans 的 reentrancy 做完了，而是把“同链回跳不该死锁”这条最核心、最基础的运行时边界，第一次真正落成了代码。
