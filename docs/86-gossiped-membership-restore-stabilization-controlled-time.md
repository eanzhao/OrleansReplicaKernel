# 86. Gossiped Membership Restore Stabilization 采用受控时间

这一篇接在 membership time convergence 和后面的 gossip/restore 验证后面，补的是 `GossipedClusterMembershipView` 在 restore 之后那条 stabilization 时间线的 deterministic coverage。

前面已经逐步做到：

- 权威 membership 的 `MembershipViewChange.CreatedAtUtc` 开始共享 `TimeProvider`
- gossip fanout / anti-entropy 的 checkpoint cursor 已经有 restore 连续性验证
- `GossipedClusterMembershipView` 本身已经有一条 manual-time 测试，验证 stabilization window 会延迟 stable status 提升

但 restore 这条线还缺一个比较具体的证明：

- checkpoint 恢复出来的 view，后续 stabilization tick 到底是不是继续跟着注入的 `TimeProvider` 走

换句话说，之前我们已经证明了：

- “活着的 view” 会按 controlled time 稳定化

但还没有直接证明：

- “restore 之后的 view” 也会按新的 controlled time 继续稳定化

所以这一轮补的是：

`让 GossipedClusterMembershipView 在 restore 之后的 stabilization tick 也拥有直接的 deterministic time 验证。`

## 1. 当前这轮具体做了什么

### 1.1 新增 `Restore_UsesConfiguredTimeProviderForStabilizationTick`

这条测试走的是一条非常具体的 membership 观察路径：

1. 原始 view 在受控时间下先观察到 `Healthy`
2. provider 前进 1 秒后，再观察到 `Suspect`
3. 此时导出 checkpoint
4. 用一个新的 `ManualTimeProvider` 从同一时刻恢复 view
5. 在 restore 后的 provider 上先推进 4 秒，确认还没跨过 5 秒 stabilization window
6. 再推进 1 秒，确认 `RunStabilizationTick()` 正式把 stable status 提升到 `Suspect`

这条测试真正证明的不是“restore 能成功”，而是：

- restore 之后 view 的 stabilization clock 仍然完全受注入的 provider 控制

### 1.2 restore 覆盖从“数据保持”推进到了“时间语义保持”

之前 `ExportCheckpointAndRestore_PreserveEpochAndObservedMembers` 主要证明的是：

- epoch 没丢
- observed member records 没丢

这一轮补上之后，restore 覆盖就不再只停留在“状态值对不对”，还开始直接覆盖：

- restore 后的稳定化时间语义对不对

## 2. 为什么这一步值得补

### 2.1 restore 后的时间边界，本来就是 membership 的一部分

对 `GossipedClusterMembershipView` 来说，checkpoint 恢复的不只是：

- 当前 epoch
- 已观察到的节点状态

还包括每条 observed record 上的：

- `LastObservedAtUtc`

而 stabilization 是否生效，本质上就是拿当前 provider 时间去和这组 observed 时间做比较。

既然如此，restore 后的时间边界就不是边角行为，而是 membership view 语义的一部分。

### 2.2 只验证 checkpoint 数据，不足以证明 restore 之后还能继续跑

如果只看“restore 之后成员列表对了没有”，我们最多只能说明：

- checkpoint 反序列化没坏

但还不能说明：

- restore 之后的 stabilization 逻辑仍然跟着受控时间向前推进

这一轮的新增测试，补上的正是这一层语义。

## 3. 当前推进到哪一层了

把 membership 这条线连起来看，现在已经逐步覆盖：

1. 权威 membership view-change timestamp 的 provider 收敛
2. gossip dissemination fanout / anti-entropy 的 checkpoint 连续性
3. gossiped view 自身在活体状态下的 stabilization window 验证
4. gossiped view restore 之后继续按 configured provider 跑 stabilization tick

第四层很关键，因为它把“恢复后的观察视图”也拉进了同一条 deterministic time 语义里。

## 4. 当前阶段还不能说成什么

这轮现在做到的是：

- `GossipedClusterMembershipView.Restore(...)` 之后的 stabilization tick 已经有 direct controlled-time test

这轮还没有做到的是：

1. 整个 membership 子系统所有 restore/stabilization 组合路径都已经穷尽
2. 多 observer、多轮 gossip、多次恢复下的所有时间断言都已经完全覆盖
3. 更高层 cluster orchestration 对 membership stabilization 的时间语义已经全部验完

所以更准确的说法不是：

- “membership restore 时间模型已经全部完成”

而是：

- “gossiped membership view 在 restore 后继续按 configured provider 稳定化，已经开始拥有直接验证”

## 5. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不仅在活体 membership/gossip 路径上开始统一和验证时间语义，也开始直接验证 restore 后的 GossipedClusterMembershipView 会继续按注入的 TimeProvider 执行 stabilization；但这仍然只是 membership 时间边界收口中的一段阶段性覆盖。`
