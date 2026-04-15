# 请求去重、迟到响应与"同一个 RequestId 不能重复执行"（第五十五篇）

## 1. 这一篇要接哪根线

前一篇已经补上了：

`owner version changed -> stale request rejected -> caller invalidate locator -> retry`

这解决的是“你发到了过期 owner”。

但还有另一类问题没有解决：

请求其实发到了对的 owner，grain 也已经执行了，只是响应没成功送回来。

这时调用方一重试，如果目标端再把同一个请求重新执行一次，业务语义马上就脏了。

最典型的例子就是：

1. 计数 grain 加一次
2. 第一次执行已经成功
3. 响应在返回路上丢了
4. 调用方重试
5. 目标端又执行一遍

结果就从“加一次”变成了“加两次”。

所以 handoff/fencing 补完以后，下一层自然要补的就是：

- request deduplication
- duplicate request replay
- stale/late response handling

## 2. 问题的本质

这里最重要的一点是：

“需要重试”不等于“允许重跑”。

真正应该重试的，是通信。

真正不应该重跑的，是已经被目标端接受并执行过的 invocation。

所以系统里其实有两条完全不同的链：

1. transport failure / response delivery failure
2. invocation execution

如果这两条链没有被拆开，runtime 很容易做出错误决定：

1. 把“响应丢了”误当成“方法没跑”
2. 把“重新发请求”误当成“重新执行业务”

重建版 Orleans 如果要把语义做干净，这两件事必须明确分开。

## 3. 最小语义

我建议先把当前实现压成四条规则：

1. 每次调用生成一个 `RequestId`
2. retry 时沿用同一个 `RequestId`
3. 目标端对同一个 `RequestId` 只允许执行一次
4. 后续重复到达的同 `RequestId` 请求，只能复用进行中的结果，或者重放已完成结果

这四条规则里，真正关键的是第 2 条。

如果 retry 时换了新 `RequestId`，那目标端根本没法知道你是在重试同一件事，还是在发一件新事。

所以 dedupe 的前提，不是“有缓存”，而是“同一逻辑请求在重试时必须沿用同一个身份”。

## 4. 目标端应该保存什么

最小实现其实只要两份表就够了：

1. in-flight requests
2. completed requests

### 4.1 in-flight

作用是：

如果同一个 `RequestId` 在第一次还没执行完的时候又到了一次，第二次不要再排队执行，而是直接 join 第一次的执行结果。

这解决的是“并发重复请求”。

### 4.2 completed

作用是：

如果同一个 `RequestId` 在第一次已经执行完之后又到了一次，第二次不要再执行，而是直接 replay 上次的 response。

这解决的是“响应丢了之后的重试”。

这两份表加起来，才是“同一个 `RequestId` 只执行一次”的完整最小闭环。

## 5. 这件事最适合落在哪一层

最干净的落点，不是 grain method，也不是 scheduler，而是 target runtime 的 request receiver。

也就是：

`ReceiveAsync(message)` 这一层。

原因很直接：

1. 这里能看见 `RequestId`
2. 这里还没真正进入 grain method
3. 这里最适合判断“这是新请求、并发重复请求，还是已完成重试请求”

如果把 dedupe 放到 grain method 层，每个 grain 都得自己处理重复执行，这会把运行时语义污染到业务层。

如果把 dedupe 放到 transport 层，transport 又不知道“执行过没有”，它只能看到消息，不知道 method side effect。

所以最合适的层，还是 target runtime。

## 6. 和 fencing 的关系

`fencing` 和 `dedupe` 很容易被写成一锅，但它们其实解决的是两种不同的问题。

### 6.1 fencing 解决什么

fencing 解决的是：

“这条消息是不是发到了已经失效的 owner”

也就是：

- 地址合法性
- owner 版本正确性

### 6.2 dedupe 解决什么

dedupe 解决的是：

“这条消息虽然发对了 owner，但它是不是一个已经执行过的请求”

也就是：

- request identity
- invocation 是否已经被接受并跑过

所以最清楚的划分是：

1. 先过 fencing
2. 再过 dedupe
3. 最后才真正进 activation/scheduler/method

这三层顺序不能反。

## 7. 当前版本最值得做的一种失败注入

最适合验证 dedupe 的，不是“请求发错节点”，而是“响应回不来”。

因为只有这种场景，才能证明：

1. 第一次其实已经执行成功
2. 调用方确实进行了 retry
3. 第二次没有再执行 grain method
4. 返回值依然能回来

最小演示可以这样做：

1. 让 grain 先在 `dev-node-2` 上运行起来
2. 发一次请求，让它正常执行
3. 注入“下一次响应在执行完成后被丢弃”
4. 调用方发请求
5. 目标端执行完成，但 transport 丢掉 response
6. source runtime 发现 response delivery failure，按同一个 `RequestId` retry
7. target runtime 命中 completed-request cache，直接 replay 旧 response

如果最后业务计数只增加一次，就说明 dedupe 成立了。

## 8. OrleansReplicaKernel 这一版怎么落

当前这版实现，我建议只做最小但完整的闭环：

1. `InProcessRuntime.InvokeAsync` 在 retry 时继续复用同一个 `RequestId`
2. `InProcessRuntime.ReceiveAsync` 增加：
   - in-flight table
   - completed table
3. duplicate request 到达时：
   - 如果第一次还在执行，就 join in-flight task
   - 如果第一次已经执行完，就 replay cached response
4. `InProcessMessageTransport` 增加一种 failure injection：
   - 允许“目标端已经执行完，但这次 response 被丢弃”
5. source runtime 把这种错误当成 retryable transport failure

这一版故意还不做：

1. response cache eviction policy
2. 跨进程 persistent request journal
3. distributed duplicate suppression
4. request cancellation 协议传播
5. stale response multiplexer
6. out-of-order response channel

也就是说，这一版的目标不是完成 Orleans 的完整消息可靠性协议，而是先把最关键的一条语义立住：

`response lost -> caller retries same request id -> target replays response -> grain method does not execute twice`

## 9. 当前版本下，“迟到响应”该怎么理解

这件事在完整系统里可以拆得更细：

1. duplicate request
2. duplicate response
3. stale response
4. response after caller timeout

但在 OrleansReplicaKernel 现在这个阶段，最值得先守住的是：

“一旦 target 已经执行过同一个 `RequestId`，后续 retry 不能再把方法跑第二遍。”

所以当前这版其实是在先解决“迟到请求导致的重复执行”。

而“迟到响应怎么在 source 侧多路复用、怎么丢弃旧 response、怎么和 timeout/cancellation 精确对齐”，这会是下一阶段的问题。

## 10. 为什么完整复刻 Orleans 必须补这层

如果一个 actor runtime 只做到：

1. 能路由
2. 能 handoff
3. 能 retry

但做不到：

4. 同一个逻辑请求只执行一次

那它在真正的分布式环境里就会很危险。

因为现实里最常见的失败，恰恰不是“请求永远发不到”，而是：

- 发到了，但确认没回来
- 执行了，但响应丢了
- 调用方不确定到底该不该重试

这时候 runtime 如果不给 invocation 一个稳定身份，不帮你守住 duplicate suppression，业务层最后一定会被迫自己补幂等。

而 Orleans 真正有价值的地方之一，就是尽量把这类协议性脏活收在 runtime 里，而不是把它们全部外溢给 grain 业务代码。
