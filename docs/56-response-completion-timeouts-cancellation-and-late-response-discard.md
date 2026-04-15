# 响应完成、超时、取消与迟到响应丢弃（第五十六篇）

## 1. 这一篇要补哪块空白

前一篇已经把 target 侧的 dedupe 补起来了：

`同一个 RequestId 到目标端之后，只执行一次`

但 source 侧还有一块很关键的语义没补全：

1. 调用方 timeout 了，source runtime 应该怎么结束等待
2. 调用方 cancel 了，remote 执行是不是也要立刻停
3. response 在 caller 放弃之后才到，应该怎么处理

如果没有 source 侧这层 response completion 管理，runtime 很容易退化成一种很含糊的状态：

- transport 返回什么，就算什么
- caller 一 cancel，整个请求链都一起停
- late response 没地方落，也没人明确丢弃

这跟 Orleans 真正想表达的语义差很远。

## 2. 最小目标

当前阶段最值得先守住的是这三条：

1. source 侧要有明确的 pending response completion
2. caller timeout / cancellation 只结束 source 的等待，不自动等于 remote execution 停止
3. late response 如果回到 source 时已经没有对应 pending completion，要被明确 discard

也就是说，先把“等待响应”从“执行业务”里拆出来。

## 3. 为什么不能继续直接等 transport 返回

如果 source runtime 只是这样写：

`await transport.SendAsync(request)`

那它实际上把三件事绑死在一起了：

1. request 发送
2. remote 执行
3. response 返回 source

一旦 caller timeout，这三件事就会被一起取消或一起中断。

但真实分布式系统里，这三件事本来就不是一件事。

最典型的现实场景是：

1. request 已经发到了 target
2. target 也已经执行了
3. source 这边等超时了
4. response 晚一点才回来

如果 source 侧没有单独的 response completion registry，这个 late response 根本没地方挂，只会在实现里变成一团模糊的副作用。

## 4. 最小模型：RequestId 和 AttemptId 分开

这一层最关键的设计点是：

`RequestId` 和 `AttemptId` 不能再混成一个东西。

### 4.1 RequestId

代表“这是同一个逻辑调用”。

它的作用是给 target 侧 dedupe 用的。

只要 source 还认为自己是在重试同一个逻辑调用，就必须复用同一个 `RequestId`。

### 4.2 AttemptId

代表“这是 source 当前正在等待的那一次发送尝试”。

它的作用是给 source 侧 response completion 用的。

因为同一个逻辑调用可以有多次 attempt：

1. 第一次 attempt 超时
2. 第二次 attempt retry
3. 第一次 attempt 的 late response 这时才回来

如果没有 `AttemptId`，source 根本分不清：

- 这是当前在等的 response
- 还是上一轮已经过期的 response

所以 current source-side waiting 必须按 `AttemptId` 跟踪，而 target-side dedupe 继续按 `RequestId` 跟踪。

## 5. Source 侧应该维护什么

最小实现只需要一张表：

- `pendingResponses[AttemptId] -> completion`

流程是：

1. 每次发一个 attempt，先注册一个 pending completion
2. transport 最终把 response 投递回来时，按 `AttemptId` 找 completion
3. 找到了，就完成它
4. 找不到，就说明 caller 已经不等了，这个 response 是 late response，直接 discard

这张表只负责“谁还在等这个 response”。

它不负责 dedupe。

dedupe 还是 target 侧 `RequestId` 的事情。

## 6. Timeout / Cancellation 的最小语义

当前阶段我建议把这件事收得非常明确：

1. caller timeout / cancellation 只影响 source 侧等待
2. remote 执行默认继续跑
3. remote response 回来时，如果 caller 已经放弃，就 discard

这跟“把 cancellation token 一路透传到 transport、scheduler、grain method，然后所有东西一起停掉”是两种完全不同的模型。

当前这版先选前者，原因是它更接近 Orleans 里的核心现实：

- 调用超时不等于对方一定没执行
- caller 放弃等待，不等于 remote side effect 自动回滚

## 7. 这一层和前一篇 dedupe 的关系

前一篇的 dedupe 是：

- target 侧：同一个 `RequestId` 不能重复执行

这一篇的 response completion 是：

- source 侧：只有当前还活着的 `AttemptId` 才能接收 response

这两层必须同时存在，整条链才完整。

如果只有 dedupe 没有 source completion：

- target 不会重复执行
- 但 source 还是不知道怎么处理 late response

如果只有 source completion 没有 dedupe：

- source 能正确 discard late response
- 但 target 还是可能把同一个逻辑调用执行两次

所以最小闭环其实是：

`RequestId for target dedupe + AttemptId for source completion`

## 8. OrleansReplicaKernel 这一版怎么落

当前这版实现里，我建议只做最小但真实的闭环：

1. `InvocationMessage` 增加 `AttemptId`
2. `InvocationResponseMessage` 也带 `AttemptId`
3. source runtime 不再直接 `await transport` 拿 response
4. source runtime 先注册 `pendingResponses[AttemptId]`
5. transport 执行完以后，把 response 显式投递回 source runtime
6. source runtime 按 `AttemptId` 完成等待
7. timeout / cancellation 时，source runtime 删除这条 pending entry
8. 后续 late response 到达时，source runtime 记一条 `discard late response`

这样就能第一次把“caller 等待”和“remote 执行结果返回”分成两条线。

## 9. 当前版本最值得跑的一条验证

最值得跑的不是网络错误，而是：

`caller timeout，但 remote 其实继续执行完了`

最小演示可以这样做：

1. 先对一个本地 grain 发一次正常请求，拿到 `count=1`
2. 再发一个 `PingSlowAsync(delay=150ms)`
3. caller 只给 50ms timeout
4. source 在 50ms 时放弃等待
5. grain 继续执行到 150ms，完成 `count=2`
6. response 回到 source 时，因为对应 `AttemptId` 已经没人等了，所以被 discard
7. 再发一次普通请求，就会看到返回 `count=3`

这条链说明两件事：

1. caller timeout 没有杀掉 remote execution
2. late response 确实被 source runtime 明确丢弃了

## 10. 当前版本故意还不做的东西

这一版还没有做：

1. 真正的 response timeout policy 配置化
2. invocation timeout 和 user cancellation 的语义拆分
3. late response metrics / eviction policy
4. source 侧 response cache
5. cancellation acknowledgment
6. distributed response channel
7. response sequencing / stale response suppression
8. callback target / observer 侧的 completion registry

也就是说，这一版的目标不是做完整的 Orleans 消息可靠性协议，而是先把 source 侧最基本的表达补上：

`I am no longer waiting for this attempt, so this response is late and must be discarded.`

## 11. 为什么完整复刻 Orleans 必须补这层

如果 runtime 最终要完整复刻 Orleans，它不能只会“把请求送过去”。

它还必须知道：

1. 哪些请求 source 还在等
2. 哪些响应已经过期
3. timeout 以后应该结束的只是等待，还是执行本身

这些问题如果不在 runtime 里明确表达，最后一定会变成业务代码里的猜测：

- “这次超时到底算没执行，还是执行了但我没拿到结果？”
- “我现在要不要重试？”
- “这个迟到响应还该不该认？”

完整复刻 Orleans，真正难的地方从来不只是 placement、membership、handoff。

还包括这种更细、更消息语义层的边界。

而这一篇补的，就是这条线的第一版骨架。
