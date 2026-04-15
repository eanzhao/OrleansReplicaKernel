# 91. Runtime Checkpoint 保留 Owner Assignment，但不保留 Activation 内存状态

这一篇接在前面的 runtime checkpoint metadata/fresh-instance 验证后面，补的是另一条更贴近多节点运行时语义的恢复边界：

- handoff / owner migration 之后的 grain directory owner assignment
- runtime checkpoint restore 之后是否还保持连续

前一轮已经开始验证：

- runtime checkpoint restore 会把 grain directory records 和 activation metadata 带回来
- 但 activation 本身仍然按 fresh-instance 语义重建

不过在多节点场景下，还有一层更具体的问题：

- 如果 grain 在 checkpoint 前已经迁到新 owner
- runtime checkpoint restore 之后，这个 owner assignment 还在不在

所以这一轮补的是：

`让 runtime checkpoint restore 在多节点 handoff 之后的 owner continuity，也拥有一条 host-level direct test；同时继续明确 activation 内存状态仍然不会被带回来。`

## 1. 当前这轮具体做了什么

### 1.1 新增 `RuntimeCheckpoint_PreservesRecoveredOwnerAssignmentAcrossRestore`

这条测试走的是一条完整多节点路径：

1. 在 `dev-node-1` 上先激活 grain 并完成第一次调用
2. 把 owner 显式迁到 `dev-node-2`
3. 再发起一次调用，让目标节点真正创建 activation，并承接 handoff 后的 owner 路径
4. 捕获 `runtime checkpoint`
5. 销毁 host，并从 checkpoint 重建
6. 直接验证：
   - restored grain directory record 仍然指向 `dev-node-2`
   - restored activation metadata 仍然落在 `dev-node-2`
7. 再次调用同一个 grain，确认虽然 owner continuity 还在，但 activation 仍然是 fresh instance，所以计数重新从 `1` 开始

这条测试把两件事同时钉住了：

- owner assignment continuity 已经存在
- activation memory continuity 仍然不存在

### 1.2 `RuntimeCheckpointStateTests` 开始用共享 helper 比较 activation directory checkpoints

前一轮在比较 activation directory checkpoint 时，已经踩到过一次“数组字段直接做记录相等会掉回引用比较”的问题。

这一轮把这层断言收成了：

- `AssertActivationDirectoriesEqual(...)`

它直接比较：

- node name 顺序
- 每个 activation directory 的 `Records`

这样后面继续补 runtime checkpoint 恢复测试时，不需要重复写一套局部比较逻辑。

## 2. 为什么这一步值得补

### 2.1 单节点 runtime metadata 恢复，不足以说明 owner migration 语义也连续

前一轮的单节点测试已经说明：

- directory record 能恢复
- activation metadata 能恢复

但单节点下没有 owner 迁移，所以还不能直接说明：

- checkpoint 前已经发生过的 owner reassignment

在 restore 之后仍然被保留下来。

这一轮补的正是这层多节点语义。

### 2.2 “owner continuity 还在” 和 “activation state 不在了” 必须一起表达

如果只验证 owner 恢复成功，很容易让人误以为：

- 既然 owner continuity 还在，activation state 也应该跟着回来

当前阶段并不是这样。

这条测试通过：

- checkpoint 前 `after-move:count=2`
- restore 后 `after-restore:count=1`

把这层边界直接表达了出来：

- 路由语义已经连续
- activation 内存语义还没有连续

## 3. 当前推进到哪一层了

把 runtime checkpoint 这条 host-level 恢复线连起来看，现在已经逐步覆盖：

1. membership dissemination continuity
2. runtime metadata continuity
3. fresh-instance activation 语义
4. handoff 之后的 owner assignment continuity

第四层很关键，因为它把“多节点 owner 路径恢复”也纳入了 host-level 直接验证。

## 4. 当前阶段还不能说成什么

这轮现在做到的是：

- runtime checkpoint restore 之后，grain directory owner assignment 已经有 direct multi-node coverage
- 同时继续证明 activation 仍然不是 full in-memory restore

这轮还没有做到的是：

1. 所有 handoff / rebalance / repeated migration 组合都已经覆盖
2. 更复杂的多 grain、多 node、多轮 checkpoint 场景已经穷尽
3. 最终完整复刻 Orleans 时的所有 activation-state recovery 能力已经全部具备

所以更准确的说法不是：

- “runtime checkpoint 已经具备完整的多节点 activation 恢复”

而是：

- “runtime checkpoint 当前阶段已经开始可靠恢复 owner assignment continuity，但 activation 仍然按 fresh-instance 语义重建”

## 5. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不仅开始在 host 层直接验证 runtime checkpoint restore 会保留多节点 handoff 后的 owner assignment，也继续明确当前阶段 activation 本身仍然不会随 checkpoint 一起恢复内存状态；但这仍然只是多节点恢复语义逐步收口中的一个阶段性边界。`
