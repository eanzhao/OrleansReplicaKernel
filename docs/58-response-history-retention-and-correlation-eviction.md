# 响应历史保留与关联回收（第五十八篇）

## 1. 这一篇补哪块

前一篇把 source 侧 response 分类补出来了：

- `accepted`
- `late`
- `stale`
- `duplicate`

但如果做到这一步就停，runtime 其实还留着一个很实在的问题：

- target 侧 `completedRequests` 会一直长
- source 侧 `sourceRequests` 也会一直长

这在原型阶段还能忍，在完整复刻 Orleans 的目标下就不行了。

因为这已经不是“功能还没补完”，而是：

`相关状态已经能表达语义了，但生命周期还没收住`

## 2. 为什么这里不能偷懒

这类状态天生是“历史相关状态”。

它们不是业务状态，也不是 activation 自身状态，而是为了这些事情临时保留的：

1. dedupe
2. stale suppression
3. duplicate suppression
4. late response classification

如果不清理：

- 内存会一直涨
- 节点活得越久，残留 request history 越多
- 观测指标越来越失真

但如果清理得太早，又会直接破坏语义：

- target 过早丢掉 `completedRequests`，同一个 `RequestId` 可能又被执行一次
- source 过早丢掉 request history，旧 response 就再也分不清是 `late`、`stale` 还是 `duplicate`

所以这里不能只说“加个缓存”，也不能只说“做个回收”。

真正要补的是：

`相关状态的保留窗口`

## 3. 最小问题模型

这一层其实有两种历史：

### 3.1 target 侧 completed request history

target 侧保留的是：

`某个 RequestId 已经执行完了，它的响应是什么`

它的目的只有一个：

`同一个 RequestId 再来时，不重新执行业务方法，而是 replay cached response`

这就是 dedupe 的历史。

### 3.2 source 侧 request correlation history

source 侧保留的是：

- 这个 request 已经推进到第几个 attempt
- 哪个 attempt 已经完成
- caller 还在不在等

它的目的不是 dedupe，而是 response classification。

也就是说，这两边虽然都叫“history”，但不是同一类东西，生命周期策略也不该混。

## 4. 为什么 retention window 是第一版最合适的方案

到这个阶段，最合适的不是一上来搞复杂的 LRU、分级清理、后台扫描线程，而是先把这件事收成一个非常清楚的模型：

`terminal request state lives for a bounded retention window`

也就是：

1. 请求还在 in-flight 时，不清
2. 请求一旦进入 terminal 状态
   - source：completed 或 caller stopped waiting
   - target：completed and cached
3. 从 terminal 时刻开始保留一个有限窗口
4. 窗口到了就清

这是最容易讲清楚、也最容易验证的第一版。

## 5. source 和 target 为什么要分开收

### 5.1 target 侧

target 侧清早了，会直接丢 dedupe 语义。

这意味着：

- 同一个 `RequestId` 重新到达时
- runtime 不知道它已经执行过
- grain 方法会再次执行

所以 target retention 的下限，本质上是：

`至少要覆盖 response replay 还可能发生的那段时间`

### 5.2 source 侧

source 侧清早了，通常不会让 grain 多执行一次，但会让 response 语义变糊。

比如旧 response 再回来时，source 只能说：

`我已经不认识这个 request 了`

这时它还能丢，但它就不再知道：

- 这是 stale
- 还是 duplicate
- 还是普通 late response

所以 source retention 更多是在保护 observability 和 protocol clarity。

## 6. 第一版最小实现怎么落

当前这版 `OrleansReplicaKernel` 最值得做的就是：

1. 给 runtime 注入 `TimeProvider`
2. 给 response history 配一个 retention window
3. target 侧 `completedRequests` 记录 `CompletedUtc`
4. source 侧 request state 记录 `UpdatedUtc`
5. 只回收 terminal state
6. in-flight state 永远不该因为 retention 到期被硬删

这里最重要的一条边界是：

`retention 只管 terminal history，不管 live invocation`

## 7. 为什么要把时间源注入进来

如果 retention 逻辑还是直接用 `DateTimeOffset.UtcNow`，测试会变得很别扭：

- 要靠真实时间等待
- 运行快一点、慢一点都可能影响断言
- 很难精确验证“窗口刚过期”和“窗口还没过期”

所以这一层最应该早点做的是：

`TimeProvider first`

这不仅是为了测试方便，也是为了后面把这套时间语义接进更多 runtime 组件时，边界能保持一致。

## 8. 第一版应该暴露什么

我不建议第一版一上来就做 metrics backend 或大而全的诊断面。

但至少要能暴露这几类计数：

- `accepted / late / stale / duplicate`
- `pending responses`
- `tracked source requests`
- `completed target requests`

原因很简单：

如果你做了 retention，却看不见它到底清了什么，那后面排问题还是要回到翻日志。

## 9. 最值得跑的一条验证

最值得验证的是：

1. 做一次远端调用
2. 让 source 侧形成 request history
3. 让 target 侧形成 completed request cache
4. 在 retention window 内查看，两个状态都还在
5. 人工推进时间
6. 超过 retention window 后再次查看
7. source 侧 tracking 清掉
8. target 侧 completed cache 清掉

这条链验证的是：

`runtime 现在不仅会长历史，还会正确地结束历史`

## 10. 这一版故意还没做什么

这一版还没有做：

1. 后台定时清理线程
2. 基于容量的 eviction
3. per-grain / per-node 的不同 retention policy
4. checkpoint 里的 response history 持久化
5. callback / observer 侧的独立 retention window
6. 更复杂的 transport ack 协议

所以这一篇补的不是“完整消息可靠性系统”，而是把 request/response 相关状态的生命周期先从“无限期”收成“有限窗口”。

## 11. 为什么完整复刻 Orleans 迟早得补这一层

只要系统里存在：

- retry
- replay cached response
- stale suppression
- duplicate handling

那历史相关状态就一定会存在。

问题从来不是“要不要保留历史”，而是：

- 保留多久
- 谁负责清
- 清掉以后丢的到底是什么语义

如果这些边界不提前讲清楚，最后 runtime 很容易退化成一种表面上功能越来越多、实际上状态生命周期越来越乱的系统。

而完整复刻 Orleans，不该只是把 feature 一条条补齐，还得把这些 feature 背后的 runtime state 也收得住。
