# 90. Runtime Checkpoint 恢复 Runtime Metadata，但不恢复 Activation 内存状态

这一篇接在前面的 host-level runtime checkpoint / membership checkpoint 恢复验证后面，补的是另一条同样需要明确钉住的边界：

- `runtime checkpoint` 当前阶段会恢复哪些 runtime 元数据
- 又有哪些东西当前阶段还不会恢复

前一轮已经开始验证：

- host 级 `runtime checkpoint` restore 之后，membership dissemination / stabilization continuity 还会继续成立

但这还不等于：

- runtime checkpoint 已经把 grain 的内存状态也完整带回来了

当前阶段 `runtime checkpoint` 确实已经会恢复：

- grain directory records
- activation metadata records
- membership / membership views / dissemination cursor

但当前阶段它还不会恢复：

- grain activation 的内存实例状态

所以这一轮补的是：

`让 runtime checkpoint restore 同时具备两类直接验证：一类验证 runtime metadata 的确被带回来了，另一类验证 activation memory 仍然不会被误恢复。`

## 1. 当前这轮具体做了什么

### 1.1 新增 `RuntimeCheckpoint_RestoresRuntimeMetadataWithoutRehydratingActivationState`

这条测试走的是一条单节点、但语义很清楚的 host 流程：

1. 启动 host，并调用一个 grain，让：
   - grain directory 产生 owner record
   - activation directory 产生 activation metadata record
2. 捕获 `runtime checkpoint`
3. 销毁 host，并用 `WithRuntimeCheckpoint(...)` 重建
4. 在恢复后的 host 上再次捕获 `runtime checkpoint`
5. 直接比较前后两次：
   - `GrainDirectory.Records`
   - `ActivationDirectories`
6. 再次调用同一个 grain，确认返回的是 fresh activation 的 `count=1`，而不是把之前那次调用的内存状态继续接上去

这条测试真正证明的是：

- runtime metadata restore 已经成立
- activation memory rehydrate 当前阶段还没有成立

### 1.2 这条测试把“恢复了什么”和“没恢复什么”同时钉住了

如果只看 grain 调用结果，我们最多只能知道：

- restore 之后系统还能继续工作

如果只看 checkpoint 结构，我们最多只能知道：

- directory / activation metadata 没丢

而这条测试把两者接在了一起：

- 结构化元数据的确恢复了
- 但 grain activation 自身还是 fresh instance

这让 runtime checkpoint 当前阶段的实际语义清晰了很多。

## 2. 为什么这一步值得补

### 2.1 runtime checkpoint 的 richer restore 语义，不能和 full in-memory recovery 混成一回事

当前阶段 `runtime checkpoint` 相比 `membership checkpoint` 的确已经 richer：

- 它带 runtime metadata
- 它带 grain directory
- 它带 activation metadata

但 richer 不等于 complete。

如果没有直接测试，很容易在讨论里把：

- “runtime metadata 已恢复”

误说成：

- “grain 内存状态也已恢复”

这一轮的价值就在于把这层边界直接钉在测试上。

### 2.2 `LocalActivationDirectory.Restore(...)` 当前阶段的设计，本来就偏向“metadata recovery + fresh instance”

从当前实现看，activation directory restore 会先把 recovered metadata 放回目录，后续收到请求时再：

- 用 recovered metadata 做 owner/fencing/last-touched 语义恢复
- 但创建的是 fresh grain instance

这条 host-level 测试把这个实现意图提升到了更高层的可验证语义：

- runtime checkpoint restore 会恢复 activation metadata
- 但不会直接恢复 activation instance memory

## 3. 当前推进到哪一层了

把 host-level checkpoint restore 这条线连起来看，现在已经逐步覆盖：

1. runtime checkpoint restore 后的 membership continuity
2. membership checkpoint restore 后的 continuity 与非 runtime rehydrate 边界
3. runtime checkpoint restore 后的 runtime metadata continuity
4. runtime checkpoint restore 不会误把 activation memory 一并带回来

第四层很关键，因为它把 runtime checkpoint 当前阶段“ richer than membership-only, but still not full activation-state recovery ”这层语义明确表达出来了。

## 4. 当前阶段还不能说成什么

这轮现在做到的是：

- host 级 runtime checkpoint restore 已经直接验证会恢复 grain directory / activation metadata
- 同时也直接验证当前阶段不会恢复 activation 内存状态

这轮还没有做到的是：

1. 更高层 grain state persistence / provider-backed state recovery 已经接进来了
2. 完整的 activation memory snapshot / replay / warm resume 已经具备
3. 最终完整复刻 Orleans 时的所有恢复层次已经全部定型

所以更准确的说法不是：

- “runtime checkpoint 已经实现了完整 activation 恢复”

而是：

- “runtime checkpoint 当前阶段已经开始稳定恢复 runtime metadata，但 activation memory 仍然是 fresh-instance 语义”

## 5. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不仅开始在 host 层直接验证 runtime checkpoint restore 会带回 grain directory 和 activation metadata，也开始明确验证当前阶段 activation 仍然按 fresh-instance 语义重建，而不是完整恢复内存状态；但这仍然只是恢复能力逐步收口中的一个阶段性边界。`
