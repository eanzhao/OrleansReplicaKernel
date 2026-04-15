# 87. Restored Membership Gossiper 的 Fanout 与 Stabilization 连续性

这一篇接在前面的 membership restore/stabilization 受控时间验证后面，补的是 `InProcessMembershipGossiper` 自己在 restore 之后的一条组合路径：

- lagging observer 继续吃 fanout 补齐
- 已经观察到新状态、但还没跨过稳定窗口的 observer，继续跑 stabilization tick

前面已经逐步做到：

- 权威 membership 的 view-change timestamp 开始共享 `TimeProvider`
- `GossipedClusterMembershipView` 自己已经能在 restore 后按 configured provider 继续跑 stabilization
- gossiper 的 fanout cursor / anti-entropy round 在 checkpoint/restore 前后已经有连续性验证

但还有一个比较值得补的缺口：

- gossiper restore 之后，能不能在同一个 tick 里同时延续 “lagging observer 补齐” 和 “pending observed state 稳定化”

换句话说，之前我们分别证明了：

- dissemination cursor 不丢
- view 自己的 stabilization clock 不丢

但还没有把这两层真正接在一起，直接证明 restore 后的 gossiper 还能把它们协同起来继续向前推进。

所以这一轮补的是：

`让 InProcessMembershipGossiper 在 restore 之后的 fanout 与 stabilization 连续性，也拥有一条直接的 deterministic test。`

## 1. 当前这轮具体做了什么

### 1.1 新增 `Gossip_Restore_ContinuesFanoutAndStabilizationUsingConfiguredTimeProvider`

这条测试构造的是一条很具体的 dissemination 场景：

1. 两个 observer 先通过第一轮 anti-entropy 同步到健康视图
2. membership 再产生一条 `node-b -> Suspect` 的 view change
3. 第二轮普通 fanout 只把这条 change 送给 `observer-a`
4. 因为 stabilization window 还没到，`observer-a` 此时只有 `ObservedStatus=Suspect`，`StableStatus` 还保持 `Healthy`
5. 这时导出 gossiper checkpoint 并 restore
6. 受控时间推进到 stabilization window 到期
7. restore 后的下一轮 gossip 同时发生两件事：
   - `observer-b` 作为 lagging observer，通过 fanout 补齐这条 change
   - `observer-a` 作为未被选中的 observer，通过 stabilization tick 把 stable status 提升到 `Suspect`

这条测试的价值在于，它不是只验证“某一边单独成立”，而是直接验证：

- dissemination continuity
- stabilization continuity

在 restore 后仍然能协同成立。

### 1.2 `CreateViews(...)` helper 开始支持显式 stabilization window

之前 `InProcessMembershipGossiperTests` 里的 view helper 固定使用：

- `TimeSpan.Zero`

这对于 fanout/anti-entropy cursor 测试足够，但不适合表达“先观察到、后稳定化”的窗口语义。

这一轮把 helper 扩成两个重载：

- 默认仍然走 `TimeSpan.Zero`
- 需要窗口语义的测试，可以显式传 `stabilizationWindow`

这样新增测试不需要局部手搓另一套 view 创建逻辑，也不会影响之前那批零窗口场景。

## 2. 为什么这一步值得补

### 2.1 restore 后真正要恢复的，不只是 cursor

`MembershipDisseminationCheckpoint` 里保存的是：

- tick number
- next fanout start index
- per-observer last delivered epoch

这些东西保证的是 dissemination cursor continuity。

但 gossiper 每次 `Gossip()` 做的事其实不只这一层，它还会：

- 对被选中的 observer 执行 `ApplyGossip(...)`
- 对未被选中的 observer 执行 `RunStabilizationTick()`

所以 restore 后如果只验证 cursor 连续，并不能完全说明 gossiper 的 runtime 语义连续。

这一轮补上的，正是这后一层。

### 2.2 “lagging observer 补齐” 和 “pending observer 稳定化” 本来就会并行发生

在真实的 membership dissemination 里，不同 observer 本来就可能同时处在不同阶段：

- 有的还没看到最新 epoch
- 有的已经看到了，但 stable status 还在等窗口

因此 restore 后最值得验证的，不是某一条单独路径，而是这种“不同 observer 处在不同 dissemination/stabilization 阶段”的组合状态能不能继续跑下去。

## 3. 当前推进到哪一层了

把 membership 这条线连起来看，现在已经逐步覆盖：

1. 权威 membership 的 timestamp provider 收敛
2. gossiped view 自己的 stabilization window deterministic 验证
3. restored gossiped view 继续按 configured provider 稳定化
4. restored gossiper 继续保持 fanout cursor 连续
5. restored gossiper 在同一 tick 里同时延续 lagging fanout 和 pending stabilization

第五层很关键，因为它把 restore 后的 dissemination 行为和 restore 后的时间语义真正接起来了。

## 4. 当前阶段还不能说成什么

这轮现在做到的是：

- `InProcessMembershipGossiper` restore 后的 fanout/stabilization continuity 已经有 direct test coverage

这轮还没有做到的是：

1. 所有 observer 拓扑、fanout 参数和 anti-entropy 周期组合都已经穷尽
2. 更高层 cluster orchestration 对 membership dissemination 的所有恢复路径都已覆盖
3. 后续真正跨进程/跨机器传播时的恢复行为已经全部定型

所以更准确的说法不是：

- “membership dissemination restore 已经全部完成”

而是：

- “restored membership gossiper 在 fanout 与 stabilization 两条语义上，已经开始拥有组合连续性验证”

## 5. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不仅开始验证 membership view restore 后的受控时间语义，也开始直接验证 restored gossiper 会继续把 lagging observer fanout 和 pending observer stabilization 协同推进；但这仍然只是 membership dissemination/recovery 覆盖持续收口中的一部分。`
