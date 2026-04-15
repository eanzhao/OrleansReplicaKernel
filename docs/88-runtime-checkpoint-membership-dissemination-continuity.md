# 88. Runtime Checkpoint 恢复后的 Membership Dissemination 连续性

这一篇接在前面的 membership restore/gossiper continuity 验证后面，补的是一条真正的 host 级 runtime-checkpoint 恢复路径。

前几轮已经逐步做到：

- `InProcessClusterMembership` 自己的 timestamp 在 restore 后会继续跟着 configured `TimeProvider`
- `GossipedClusterMembershipView` 在 restore 后会继续按 configured provider 跑 stabilization tick
- `InProcessMembershipGossiper` 在 checkpoint/restore 前后，已经有 fanout cursor 和 fanout/stabilization continuity 的单测

但还缺一个更贴近真实运行时装配的验证：

- `OrleansReplicaKernelHost.CaptureRuntimeCheckpoint()`
- `OrleansReplicaKernelBuilder.WithRuntimeCheckpoint(...)`

把整套 membership checkpoint、view checkpoint、gossiper checkpoint 一起恢复之后，host 级的 dissemination 行为能不能继续无缝往前跑。

所以这一轮补的是：

`让 runtime-checkpoint restore 后的 membership dissemination continuity，也拥有一条 host 级 deterministic test，并顺手把 host 的 membership 观察能力从字符串描述提升成结构化 snapshot。`

## 1. 当前这轮具体做了什么

### 1.1 `OrleansReplicaKernelHost` 新增 `GetMembershipViewSnapshot(...)`

之前 host 只有：

- `DescribeMembershipView(observerNodeName)`

它适合 demo 打日志，但不适合测试做结构化断言。

这一轮新增了：

- `GetMembershipViewSnapshot(observerNodeName)`

返回的是结构化的 `ObservedClusterMemberRecord` 列表；`DescribeMembershipView(...)` 也改成直接复用这个 snapshot。

这一步的意义不是“日志字符串不能用”，而是：

- demo 可以继续看字符串
- 测试和更高层 orchestration 可以开始直接看 membership view 的真实结构

### 1.2 新增 host 级 runtime-checkpoint 恢复测试

新增的 `RuntimeCheckpoint_RestoresMembershipFanoutCursorAndPendingStabilization` 走的是一条完整 host 流程：

1. host 先完成第一轮 anti-entropy，把两个 observer 拉到健康基线
2. 手动把 `dev-node-2` 标成 `Suspect`
3. 下一轮普通 fanout 只把这条 change 送给 `dev-node-1`
4. 此时：
   - `dev-node-1` 已经 observed `Suspect`，但 stable 仍是 `Healthy`
   - `dev-node-2` 还没看到这条 change
5. 捕获 `runtime checkpoint`
6. 释放 host，并用 `WithRuntimeCheckpoint(...)` 重建 host
7. 受控时间推进到 stabilization window 到期
8. restore 后的下一轮 gossip 同时发生两件事：
   - `dev-node-2` 通过 fanout 补齐 lagging change
   - `dev-node-1` 通过 stabilization tick 把 stable status 提升到 `Suspect`

这条测试证明的是：

- runtime checkpoint 恢复的不只是 membership 数据
- 也不只是 dissemination cursor
- 而是 host 级 membership dissemination/stabilization 的运行连续性

## 2. 为什么这一步值得补

### 2.1 host-level restore 才是更接近真实运行时的边界

前面的 membership / view / gossiper 单测都很有价值，但它们验证的是子组件边界。

而真正的 runtime restore 路径，走的是：

- cluster membership checkpoint
- per-observer membership view checkpoints
- dissemination checkpoint
- builder 重建
- host 再次驱动 probe/gossip/monitoring

如果这一层没有直接覆盖，我们最多只能说“零件各自看起来没坏”，还不能直接说“装起来以后 continuity 也没断”。

### 2.2 结构化 snapshot 比字符串描述更适合成为后续测试基线

之前如果要在 host 测试里看 membership view，只能靠：

- `DescribeMembershipView(...)`

这对日志很友好，但对断言不够稳，也不够可组合。

把 host 的 membership 观察能力补成结构化 snapshot 后，后面无论是：

- runtime-checkpoint 恢复
- monitoring round 验证
- 更高层 cluster orchestration 测试

都可以直接对真实结构做断言，而不是继续解析字符串。

## 3. 当前推进到哪一层了

把 membership restore 这条线连起来看，现在已经逐步覆盖：

1. 权威 membership timestamp 的 provider 收敛
2. restored membership view 的 stabilization 时间语义
3. restored gossiper 的 fanout/stabilization continuity
4. host runtime-checkpoint restore 后的 dissemination/stabilization continuity

第四层很关键，因为它首次把“零件级 restore 正确”推进到了“整机级 restore 继续运行正确”。

## 4. 当前阶段还不能说成什么

这轮现在做到的是：

- host 级 runtime-checkpoint restore 之后，membership dissemination continuity 已经有 direct test coverage
- host 对 membership view 的观察开始拥有结构化 snapshot API

这轮还没有做到的是：

1. runtime checkpoint 的所有子系统恢复路径都已经在 host 层完全穷尽
2. 更复杂的 multi-node failure / restart / repeated checkpoint 组合已经全部覆盖
3. 真正跨进程、跨机器传播下的恢复行为已经完全定型

所以更准确的说法不是：

- “runtime checkpoint 恢复已经全部完成”

而是：

- “host 级 membership dissemination continuity 已经开始拥有 direct restore verification”

## 5. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不仅在 membership 子组件层开始验证 restore 后的时间与传播连续性，也开始在 host/runtime-checkpoint 这一层直接验证 dissemination/stabilization 会继续无缝推进；但这仍然只是整机级恢复覆盖逐步收口的一部分。`
