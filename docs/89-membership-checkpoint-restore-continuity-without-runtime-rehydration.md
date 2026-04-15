# 89. Membership Checkpoint 恢复后的连续性与非 Runtime Rehydrate 语义

这一篇接在前面的 host-level runtime checkpoint 恢复验证后面，补的是另一条容易混淆、但边界上必须说清楚的恢复路径：

- `CaptureMembershipCheckpoint()`
- `WithMembershipCheckpoint(...)`

前一轮已经开始验证：

- runtime checkpoint restore 之后，host 级 membership dissemination/stabilization continuity 会继续成立

但 `membership checkpoint` 和 `runtime checkpoint` 不是一回事。

`membership checkpoint` 当前阶段恢复的是：

- cluster membership
- per-observer membership view
- dissemination cursor

它**不**恢复的是：

- grain directory runtime state
- activation metadata
- activation 内存状态

所以这一轮补的是：

`让 membership-only checkpoint restore 同时具备两类直接验证：一类验证 membership dissemination continuity 还在，另一类验证 runtime state 没有被误恢复。`

## 1. 当前这轮具体做了什么

### 1.1 新增 `MembershipCheckpoint_RestoresMembershipContinuityWithoutRehydratingRuntimeState`

这条测试走的是一条完整 host 流程：

1. 启动 host，并调用一个 grain，让 grain directory 真正进入非空状态
2. 把 `dev-node-2` 标成 `Suspect`
3. 让一轮普通 fanout 只把这条 change 送给 `dev-node-1`
4. 捕获 `membership checkpoint`
5. 销毁 host，并用 `WithMembershipCheckpoint(...)` 重建
6. 验证 grain directory 已经回到空状态
7. 受控时间推进到 stabilization window 到期
8. restore 后的下一轮 gossip 同时继续：
   - `dev-node-2` 的 lagging fanout 补齐
   - `dev-node-1` 的 pending stabilization
9. 再次调用同一个 grain，确认它是 fresh activation，而不是从上一个 host 的 runtime state 里被 rehydrate 出来

这条测试的关键不只是“membership 还能继续传播”，而是把两层语义明确分开：

- membership continuity 继续成立
- runtime state 不会因为 membership checkpoint 被误恢复

### 1.2 `RuntimeCheckpointMembershipTests` helper 同时支持两种 checkpoint restore

这一轮把测试 helper 扩成了同时支持：

- `runtimeCheckpoint`
- `membershipCheckpoint`

这样两条 host-level restore 测试可以共享同一套构建逻辑，但边界仍然清楚：

- 传 runtime checkpoint，就是整机级恢复
- 只传 membership checkpoint，就是 membership-only 恢复

## 2. 为什么这一步值得补

### 2.1 `membership checkpoint` 和 `runtime checkpoint` 的边界必须靠测试钉住

如果没有直接测试，很容易在实现推进过程中让这两条恢复路径的边界变得模糊：

- 要么误以为 membership-only restore 也应该把 runtime state 带回来
- 要么反过来误把 runtime checkpoint 的 richer 恢复能力削平到 membership-only 的级别

这一轮的价值就在这里：把这两条路径的当前阶段语义明确钉在测试上。

### 2.2 “不恢复什么”也是恢复语义的一部分

当前阶段 `membership checkpoint` 恢复的重点，是让 cluster membership 自己能继续跑，而不是把整个 runtime 一并还原。

所以对于这条路径来说，下面这件事不是副作用，而是语义本身：

- restore 后 grain directory 为空
- 同 key grain 再次调用会产生 fresh activation

把这层也纳入直接验证，后面扩展更完整的恢复能力时，边界才不会被悄悄改坏。

## 3. 当前推进到哪一层了

把 host-level restore 这条线连起来看，现在已经逐步覆盖：

1. runtime checkpoint restore 后的 membership dissemination continuity
2. membership checkpoint restore 后的 dissemination/stabilization continuity
3. membership checkpoint restore 不会误 rehydrate runtime state

第三层很关键，因为它把“恢复得了什么”和“当前阶段刻意还不恢复什么”都明确表达出来了。

## 4. 当前阶段还不能说成什么

这轮现在做到的是：

- `WithMembershipCheckpoint(...)` 的 host-level 恢复行为已经有 direct test coverage
- membership-only restore 与 runtime restore 的当前边界已经开始拥有明确验证

这轮还没有做到的是：

1. 所有 runtime 子系统都已经有各自独立的 membership-only / runtime restore 对照验证
2. 更复杂的 repeated checkpoint / partial failure / rolling restart 组合都已经覆盖
3. 最终完整复刻 Orleans 时的所有恢复层级已经全部定型

所以更准确的说法不是：

- “checkpoint 恢复语义已经全部完成”

而是：

- “membership-only restore 的连续性和非 runtime rehydrate 边界，已经开始拥有 host-level 直接验证”

## 5. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不仅开始在 host 层直接验证 membership checkpoint restore 后的 dissemination continuity，也开始明确验证这条路径当前阶段只恢复 membership 语义，而不会误把 runtime state 一并 rehydrate；但这仍然只是恢复边界逐步收口的一部分。`
