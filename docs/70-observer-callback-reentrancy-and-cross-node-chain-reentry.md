# Observer Callback Reentrancy 与跨节点 Chain Reentry（第七十篇）

这一篇接在 `69` 后面，看的是 request-chain reentrancy 真正碰到 callback/observer 时，会不会还成立。

`69` 解决的是：

- 同一个 activation 在一条逻辑调用链里回调自己

但真实系统里更麻烦的通常不是最简单的 self-call，而是这种链：

1. source node 调 remote grain
2. remote grain 在自己的 turn 里调用 source node 上注册的 observer / callback target
3. observer / callback target 在处理这个回调时，又反过来调用原来的 grain

如果 runtime 看不懂“这还是同一条调用链”，那这条链会非常容易卡死。

## 1. 这条链为什么天然容易死锁

因为原 grain 的执行顺序大概是：

1. `PingWithObserverAsync(...)` 进入原 grain 的独占 turn
2. grain 内部 `await observer.OnEchoAsync(...)`
3. observer invocation 发到 source node callback target
4. callback target 在 `OnEchoAsync(...)` 里再调回原 grain

这时原 grain 还在等 callback 返回。

如果回调里再打回来的请求被当成“一个普通新请求”，那它就只能排在当前独占 turn 后面。结果就是：

- 原 grain 在等 callback
- callback 在等 nested grain call
- nested grain call 又在等原 grain 先结束

这就是最典型的“跨节点回跳自锁”。

## 2. 为什么 `69` 的实现现在能吃住这条链

因为这条链虽然跨了节点、跨了 callback target，但本质上还是一条逻辑调用链。

当前实现里，关键点有三个：

### 2.1 原 grain 发 observer callback 时会沿用当前 request chain

在 grain activation 里调用 observer reference 时，`ActivationExecutionContext.CurrentRequestChainId` 还在。

所以这次 observer invocation 发出去时，不会新开一条陌生的 chain，而是继续沿用当前 chain。

### 2.2 callback target activation 处理 observer 时，也会保留这条 chain

callback target 现在本质上也是 activation。

所以 source node 上的 observer 实现跑起来时，也是在 activation execution context 里，`CurrentRequestChainId` 不会丢。

### 2.3 callback target 再调回原 grain 时，scheduler 能识别这是同链回跳

于是 observer 里如果再通过 grain reference 调原 grain：

- source runtime 会带着同一个 `RequestChainId`
- remote grain 的 scheduler 会看到：
  - 当前独占 turn 还没结束
  - 但新请求属于同一个 chain

所以它会按 request-chain reentrancy 放行，而不是把这个请求傻傻排在队尾。

## 3. 这轮 demo 怎么做的

这次我专门加了一个 observer：

- `ReentrantCallbackEchoObserver`

它的行为非常直接：

1. 收到原 grain 的 callback
2. 先记录 callback 传来的值
3. 再通过一个 `IEchoGrain` 引用，反过来调原 grain 的 `PingAsync("callback-reenter")`
4. 把 nested 调用的返回结果也记下来

然后 demo 里会跑这条链：

1. 把 `echo/callback-reentrant` owner 挪到远端 `dev-node-2`
2. source node 上注册一个 `ReentrantCallbackEchoObserver`
3. 调 `PingWithObserverAsync("callback-reentrant", observer)`
4. 远端 grain 先执行自己的 turn
5. 再 callback 到 source node observer
6. observer 再打回远端原 grain

现在这条链会成功跑完，而不是挂住。

## 4. 跑通之后能看到什么结果

这条链的计数变化很有代表性：

1. 原 grain 先处理 `PingWithObserverAsync`，`count = 1`
2. observer 收到值：`observer:callback-reentrant:count=1`
3. observer 再调回原 grain `PingAsync("callback-reenter")`
4. 这个 nested 调用作为同链重入被放进当前独占 activation，`count = 2`
5. observer 记录 nested 结果：`echo:callback-reenter:count=2`
6. 原始 `PingWithObserverAsync` 返回时，看到的也是 `count = 2`

也就是说，这条链现在会得到三份非常关键的结果：

- observer 收到：`observer:callback-reentrant:count=1`
- nested grain 调用返回：`echo:callback-reenter:count=2`
- 外层 grain 方法返回：`echo:callback-reentrant:count=2`

这三份结果合起来，能证明两件事：

1. callback 确实发生了
2. callback 里的回跳不是排队等外层 turn 结束，而是真的重入成功了

## 5. 这轮真正验证了什么边界

这一轮验证的不是“observer 能不能回调”，那件事我们前面已经做过了。

这轮验证的是更深一层的边界：

- `callback target` 不是 request-chain 的终点
- request-chain 可以穿过 callback target 再继续往前走
- callback 里的 nested grain call，和原 grain turn 之间，已经能建立正确的重入关系

这件事非常关键，因为 Orleans 里最麻烦的一批重入问题，很多都不是纯 self-call，而是这种“回调把链路折回来”的场景。

## 6. 这还不等于 callback 调度模型已经完成

这里也要讲清楚。

这一轮已经做到的是：

- callback/observer 这条跨节点回跳链，已经能共享 request chain
- 同链回跳命中原 grain activation 时，已经能安全重入

这一轮还没做到的是：

- callback target 的更细粒度调度分类
- observer callback 与方法级 interleaving 的组合语义
- client/gateway 参与时的 callback 路径
- timer/reminder/system target 回跳时的调度分类
- Orleans 那套更完整的 observer/object reference 生命周期与调度规则

所以这轮的准确定位是：

- 我们已经证明 request-chain reentrancy 不只在 self-call 上成立
- 它现在已经能穿过 callback/observer 这条真实得多的链路

## 7. 为什么这轮对完整复刻 Orleans 很重要

因为从 runtime 角度看，actor 系统真正难的不是“串行执行一个 turn”，而是：

- turn 在等待远端
- 远端又把调用折回来
- 这时系统到底该不该让它回来

如果这个问题答不好，后面所有更复杂的能力都会变得不稳：

- observer
- callback target
- streams consumer callback
- reminder / timer 回跳
- client-side callback

所以 `70` 这篇真正标记的是：

`OrleansReplicaKernel` 现在已经不只是“同 grain 自调用可重入”，而是第一次把 request-chain reentrancy 真正推进到了跨节点 callback 链这一级。后面再往 Orleans 靠时，才算终于踩进了真正难的那一段。
