# 83. Invocation 测试采用 Host Timing API 与 Semantic Observation

这一篇接在 `80` 和 `82` 后面，补的是 runtime tests 自己对 host timing API 的采用。

前几轮已经分别把这些边界逐步收起来了：

- host 暴露统一 `TimeProvider`
- host 提供 `DelayAsync(...)` / `CreateTimeoutSource(...)` / `GetElapsedTime(...)` / `WaitForAsync(...)`
- demo orchestration 开始把 observation sleep 换成语义化 wait
- 测试侧开始形成共享的 dispatch/progress helper

但在 `InvocationResponseSemanticsTests` 里，还留着一组不太一致的写法：

- 有些场景已经开始用 host helper
- 有些 non-manual 路径还在局部 `Task.Delay(...)`
- interleaving elapsed 仍然在自己测

所以这一轮补的，是把这组 runtime tests 也往 host timing API 上收。

## 1. 当前这轮具体做了什么

### 1.1 disposition 观察改成直接等待目标状态成立

下面这些测试现在不再靠固定观察窗口：

- late response discard
- stale replay discard
- duplicate response discard

它们现在改成直接等待：

- `LateResponses == 1`
- `StaleResponses == 1`
- `DuplicateResponses == 1`

也就是说，测试开始显式等“语义结果已经出现”，而不是“先睡一段时间，再假定结果应该已经出现”。

### 1.2 timer / idle 相关等待开始走 host helper

同样地：

- activation-owned timer snapshot
- deactivation 后的 timer cancel 观察
- idle collect 前的等待窗口

现在也开始直接使用：

- `host.WaitForAsync(...)`
- `host.DelayAsync(...)`

这让测试代码和 demo orchestration 用的是同一套 host timing surface，而不是各自再写一遍局部等待逻辑。

### 1.3 interleaving elapsed 观测改成 host timestamp/elapsed

`GeneratedGrainImplementationMetadata_CanAllowInterleavingForSelectedMethods` 现在也不再自己拿 `Stopwatch` 计时，而是改成：

- `host.GetTimestamp()`
- `host.GetElapsedTime(...)`

这件事的重要性在于，它让非 manual-time 测试也开始和前面那套 host timing API 对齐，而不是把 elapsed measurement 继续留在局部实现里。

## 2. 为什么这一步值得单独推进

### 2.1 host timing API 如果只被 demo 用，边界还不算真正成立

前面已经把 host timing API 建好了，也开始让 `Program` 用它。

但如果测试还继续大量手写：

- `Task.Delay(...)`
- `Stopwatch`
- 局部 observation wait

那 host timing API 仍然更像“demo convenience layer”，而不是项目里真正形成共识的 orchestration surface。

这一轮的价值就在于：

- runtime tests 也开始直接使用它

### 2.2 runtime tests 是很重要的调用语义消费者

`InvocationResponseSemanticsTests` 不是外围测试，它验证的正是：

- response discard
- retry ordering
- timer/turn interaction
- interleaving visibility

既然这些测试本身就在检验调用/响应/调度语义，那么它们采用统一的 host timing/wait surface，会让“行为语义”和“观察语义”更一致。

## 3. 当前推进到哪一层了

如果把 `78` 到 `83` 连起来看，host timing 这条线已经逐步走到这里：

1. host 提供 timing helper
2. host 提供 wait/poll helper
3. demo orchestration 开始采用 semantic wait
4. 测试层开始形成共享 dispatch/progress helper
5. runtime invocation tests 也开始直接采用 host timing/wait API

第五层很关键，因为它说明这套 API 不只是“存在”，而是开始同时约束：

- demo
- test

这两类最直接的 orchestration consumer。

## 4. 当前阶段还不能说成什么

这轮做到的是：

- 一批关键的 invocation tests 已经开始直接走 host timing API

这轮还没有做到的是：

1. 所有测试都已经完全迁移到 host/helper 驱动
2. 所有 `WaitAsync(TimeSpan.FromSeconds(1))` 都已经被语义化包装
3. 更正式的 test harness / host harness 层次已经完全定型

所以更准确的说法不是：

- “测试层已经完全统一到了最终 API”

而是：

- “关键的 invocation response tests 已经开始采用 host timing/wait surface”

## 5. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不只让 demo orchestration 使用 host timing API，也开始让关键的 invocation/runtime tests 直接采用这套 delay、elapsed 和 semantic wait surface；但这仍然只是宿主与测试调用边界收口的一部分。`
