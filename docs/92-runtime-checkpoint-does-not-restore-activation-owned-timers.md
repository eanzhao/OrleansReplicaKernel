# 92. Runtime Checkpoint 不恢复 Activation-Owned Timer

这一篇接在前面的 runtime checkpoint restore 边界验证后面，补的是 timer 这条很容易被误会的恢复语义。

前几轮已经逐步钉住：

- runtime checkpoint 会恢复 grain directory 和 activation metadata
- runtime checkpoint 会保留 handoff 之后的 owner assignment
- 但 activation 内存状态当前阶段不会恢复，restore 后仍然是 fresh instance

不过 activation-owned timer 还有一层单独值得明确的边界：

- timer 注册虽然属于 activation 生命周期的一部分
- 但当前阶段 runtime checkpoint 并不会把 timer registration 一起带回来

所以这一轮补的是：

`让 runtime checkpoint restore 后“activation-owned timer 不会被恢复，也不会在 restore 后补触发”拥有一条 host-level direct test。`

## 1. 当前这轮具体做了什么

### 1.1 新增 `RuntimeCheckpoint_DoesNotRestoreActivationOwnedTimers`

这条测试走的是一条非常具体的 host 路径：

1. 在受控时间下启动 host
2. 对某个 grain 注册一个 80ms 的 one-shot activation-owned timer
3. 在 timer 触发前捕获 `runtime checkpoint`
4. 销毁 host，并用 `WithRuntimeCheckpoint(...)` 重建
5. 受控时间直接推进 200ms，已经明显越过原本的 timer due time
6. 再次访问同一个 grain，读取 timer snapshot 并发起一次普通调用

断言的是：

- timer snapshot 仍然是 `<none>`
- 后续第一次普通调用返回的是 `count=1`

这说明 restore 后没有发生两件事：

- 旧 timer registration 被带回来
- 旧 timer 在 restore 之后按“补账”方式补触发

### 1.2 这条测试把 timer 从“activation 不恢复内存状态”里单独拿出来验证了

前一轮其实已经间接说明：

- activation instance 自身不会恢复

但 timer 是 activation-owned 的异步行为，如果没有专门测试，还是很容易让人误以为：

- activation memory 不恢复，不代表 timer registration 也不恢复

这一轮的价值就在这里：把 timer 这层边界单独钉出来。

## 2. 为什么这一步值得补

### 2.1 timer 属于 activation 生命周期，但不是 activation metadata 本身

当前阶段 runtime checkpoint 里带回来的 activation 相关信息，本质上还是：

- grain id
- owner version
- last touched
- 一些路由/恢复所需的元数据

它并不包含：

- 已注册 timer 的 due time / period / callback identity

所以当前阶段更准确的说法应该是：

- activation metadata continuity 已经开始成立
- activation-owned timer continuity 还没有成立

### 2.2 “不补触发”也属于当前阶段语义的一部分

如果 restore 之后 timer snapshot 是空的，但 runtime 又偷偷把“过期 timer”在首次恢复时补执行一遍，语义其实仍然会变得很混乱。

这条测试通过在 restore 后直接把时间推进到远超 due time，再检查：

- snapshot 仍然为空
- 第一次普通调用仍然从 `count=1` 开始

把这一层也明确表达了出来：

- 当前阶段不仅不恢复 timer registration
- 也没有任何 replay / catch-up firing 语义

## 3. 当前推进到哪一层了

把 runtime checkpoint 这条 host-level 恢复线连起来看，现在已经逐步覆盖：

1. membership dissemination continuity
2. runtime metadata continuity
3. owner assignment continuity
4. activation 仍然按 fresh-instance 语义重建
5. activation-owned timer 不会在 restore 后继续存在或补触发

第五层很关键，因为它把 activation lifecycle 里一个非常具体的异步子系统也单独纳入了恢复边界。

## 4. 当前阶段还不能说成什么

这轮现在做到的是：

- runtime checkpoint restore 之后，activation-owned timer 当前阶段不会被恢复
- 也不会出现 restore 后的 timer catch-up firing

这轮还没有做到的是：

1. 更完整的 timer snapshot / replay / durable timer 语义已经具备
2. reminder / persistent timer / provider-backed 调度已经接进来了
3. 最终完整复刻 Orleans 时的 timer/recovery 组合语义已经全部定型

所以更准确的说法不是：

- “checkpoint 恢复已经完整覆盖 timer 子系统”

而是：

- “runtime checkpoint 当前阶段已经明确不会恢复 activation-owned timer，这个边界现在开始拥有 host-level 直接验证”

## 5. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不仅开始在 host 层直接验证 runtime checkpoint restore 会保留 directory/owner 等 runtime metadata，也开始明确验证当前阶段 activation-owned timer 不会被恢复或补触发；但这仍然只是 timer/recovery 语义逐步收口中的一个阶段性边界。`
