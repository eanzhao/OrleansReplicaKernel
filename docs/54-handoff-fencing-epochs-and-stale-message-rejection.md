# Handoff 围栏、Owner Epoch 与过期消息拒绝（第五十四篇）

## 1. 这一篇要解决什么

前一篇已经把 `quiesce -> drain -> capture state -> move owner` 这条 handoff 顺序边界立起来了。

但只有 `quiesce/drain` 还不够。

原因很简单：

旧 owner 上的 activation 安静下来，不等于旧消息就消失了。

只要系统里还存在下面任意一种情况，迟到消息就还会出现：

1. 调用方命中了旧 locator cache
2. 请求已经发出，但还在 transport 里飞
3. 旧 owner 刚完成 handoff，迟到请求才到达
4. 目标节点还没来得及创建新 activation，旧版本消息先撞上来了

如果没有 fencing，这些请求会把 handoff 语义重新打脏：

1. 旧 owner 可能被误唤醒
2. 已经下线的 activation 可能被重新创建
3. 新旧 owner 可能在不同版本上分别执行一次
4. warm handoff state 可能被旧消息和新消息交叉消费

所以 handoff 真正想成立，必须再补一层：

- owner version / fence token
- stale request rejection
- 调用方收到 stale 之后做 locator invalidate + retry

这才是“旧 owner 已经失效”在消息层的真正表达。

## 2. 最小语义

我建议把这件事压成一条很窄的规则：

1. 每次 owner 变化，都让 grain 的 owner version 单调递增
2. 每条 invocation 都带上它命中 locator 时看到的 owner version
3. activation directory 只接受 `message.ownerVersion >= localFenceVersion`
4. 如果 `message.ownerVersion < localFenceVersion`，直接拒绝为 stale
5. 调用方收到 stale 后，只做两件事：
   - invalidate locator
   - 重新路由再发一次

这里最关键的一点是：

fencing 不是 transport 的职责，也不是 scheduler 的职责。

它是 owner 语义层对消息层做的一个硬约束。

## 3. Token 应该挂在哪一层

最自然的落点不是 activation instance 本身，而是 `GrainAddress`。

原因是：

1. locator 本来就负责把 `grainId -> owner` 解析成地址
2. owner version 本来就是 grain directory record 的一部分
3. invocation 在真正发送之前，本来就要先命中一次 locator

所以最干净的链是：

`grain directory owner record(version) -> locator cache -> GrainAddress(ownerVersion) -> InvocationMessage -> activation directory fence check`

这样做的好处是：

1. version 跟地址一起流动，不需要额外侧带状态
2. stale 是“地址过期”，不是“业务执行失败”
3. retry 可以收在 runtime 里，不需要调用方知道内部协议

## 4. 哪一侧负责拒绝 stale

最应该做拒绝的地方，是目标节点上的 activation directory。

不是 transport。

也不是 grain method。

原因是：

1. transport 只知道消息到了哪台 node，不知道这台 node 现在是不是这个 grain 的合法 owner
2. grain method 已经太晚了，到了 method 说明 activation 都已经被建起来了
3. activation directory 正好是“要不要复用旧 activation / 要不要创建新 activation”的门口

所以 stale reject 应该发生在：

`DispatchLocalAsync -> activationDirectory.GetOrCreate(address) -> fence check`

一旦发现请求版本落后：

1. 不复用 activation
2. 不新建 activation
3. 直接返回一个 stale error

这样旧 owner 就不会被迟到消息重新点燃。

## 5. Source node 和 Target node 分别要做什么

handoff 时两边都要立 fence，但目的不一样。

### 5.1 旧 owner

旧 owner 要做的是：

1. 把本地 fence version 推进到新的 owner version
2. 拒绝所有更旧版本的迟到请求
3. 然后再让旧 activation 下线

这一步的意义是：

即使旧 activation 还没完全 dispose，迟到的旧消息也已经过不了门。

### 5.2 新 owner

新 owner 要做的是：

1. 先知道自己现在应该接受哪个 owner version
2. 如果有 staged handoff state，把它和这个 version 绑定
3. 只允许同版本请求去消费这份 staged state

否则会出现一个很脏的问题：

一次 v2 handoff 还没真正落地，系统又已经进入 v3；这时如果 v2 的迟到请求到达新 owner，它可能会错误消费旧的 staged state。

所以 target 侧也要有 fence。

## 6. Retry 应该怎么收

收到 stale 之后，不应该做复杂补偿。

最小闭环其实就两步：

1. runtime invalidate locator
2. runtime retry invocation

因为 stale 的本质不是业务失败，而是路由命中了过期地址。

这和下面两类东西不同：

1. remote execution error：说明请求已经到达合法 owner，并在那边执行失败
2. timeout / node unavailable：说明 transport 或节点可用性出了问题

stale reject 既不是业务异常，也不是 transport 异常。

它更像是“你拿着过期门牌号走错门了”。

## 7. OrleansReplicaKernel 这一版怎么落

当前这版实现我建议只做最小闭环，不一口气上完整分布式协议。

### 7.1 当前版本要加的东西

1. `GrainAddress` 带 `OwnerVersion`
2. `DirectoryGrainLocator` 把 owner record 的 version 带进地址
3. `InvocationMessage` 带着这个版本一路发下去
4. `LocalActivationDirectory` 维护每个 grain 的 `fenced owner version`
5. `MoveOwnerAsync` 在 source 和 target 两侧都推进 fence
6. 旧版本请求命中 activation directory 时，抛 `StaleGrainAddressException`
7. `InProcessRuntime` 收到这个异常后，做 `invalidate + retry`

### 7.2 当前版本故意还不做的东西

1. 真正跨进程 transport 上的 fencing header
2. node 间的 fence persistence
3. distributed grain directory 上的严格 owner lease
4. 多副本目录和一致性确认
5. stale response dedup / causal ordering
6. 真正的 request forwarding / reply forwarding 协议

所以这一版的目标不是“完成 Orleans 的 fencing 全貌”，而是先把最核心的因果链跑通：

`owner version changed -> old node fenced -> stale request rejected -> caller invalidates locator -> request retries to new owner`

## 8. 演示上最值得跑的一条链

最适合 demo 的场景不是“简单 owner move”，而是“旧请求正在路上，owner 已经切走”。

最小演示可以这样做：

1. grain 先在 `dev-node-2` 上稳定跑起来
2. 对 `dev-node-2` 的下一次请求注入 transport delay
3. 这时发出一个请求，它会带着旧的 owner version 在路上飞
4. 在它真正到达之前，把 owner 切到 `dev-node-3`
5. `dev-node-2` 收到迟到请求时，发现版本已经落后，于是 reject stale
6. `dev-node-1` 上的 runtime 收到 stale error，invalidate locator 并重试
7. retry 命中新 owner `dev-node-3`

这一条链如果能在日志里明确跑出来，就说明这层语义已经立住了。

## 9. 重建版 Orleans 为什么必须有这层

如果目标是完整复刻 Orleans，而不是只做一个“差不多能跑”的 actor runtime，这层不能省。

因为一旦系统开始支持：

1. owner relocation
2. placement change
3. rebalancing
4. membership 传播延迟
5. retry

那 stale message 就不是边角 case，而是主路径会自然遇到的现实。

没有 fencing，handoff 只是“看起来像切过去了”。

有了 fencing，handoff 才真正从“activation 生命周期事件”变成“带版本约束的所有权切换协议”。
