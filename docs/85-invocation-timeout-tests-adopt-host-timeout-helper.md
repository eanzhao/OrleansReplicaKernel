# 85. Invocation Timeout 测试采用 Host Timeout Helper

这一篇接在前面的 host timing / invocation test 收口后面，补的是 `InvocationResponseSemanticsTests` 里最后两处还在直接碰 timeout 构造和默认时间源的测试基线。

前几轮已经逐步做到：

- host 提供 provider-aware 的 `DelayAsync` / `CreateTimeoutSource` / `GetTimestamp` / `WaitForAsync`
- demo 和大部分 runtime tests 都已经开始统一走 host timing helper
- manual-time 场景已经能在同一条 `TimeProvider` 时间线上验证 caller timeout、transport delay、retry backoff 和 observation wait

但 `InvocationResponseSemanticsTests` 里还留着两个小而突兀的缺口：

- baseline caller-timeout 测试还在直接 `new CancellationTokenSource(timeout)`
- 默认 `CreateHost()` 还在测试里显式把 host 钉死到 `TimeProvider.System`

这两个点虽然不大，但会让测试层对“timeout 应该怎么创建、默认时间源应该由谁决定”仍然保留一条额外分叉。

所以这一轮补的是：

`让 invocation timeout tests 自己也完全走 host timeout helper，并把默认 host 时间源交还给 builder 的默认路径。`

## 1. 当前这轮具体做了什么

### 1.1 caller-timeout tests 不再直接 new `CancellationTokenSource`

`CallerTimeout_DiscardsLateResponse_ButExecutionStillCompletes` 之前直接写：

- `new CancellationTokenSource(TimeSpan.FromMilliseconds(50))`

`CallerTimeout_UsesConfiguredTimeProvider_AndStillDiscardsLateResponse` 之前直接写：

- `new CancellationTokenSource(TimeSpan.FromMilliseconds(50), host.TimeProvider)`

现在两条测试都统一改成：

- `host.CreateTimeoutSource(...)`

这意味着 invocation timeout tests 不再自己分叉出一套 timeout 构造协议，而是和 demo/runtime orchestration 一样，统一通过 host 暴露的 provider-aware API 创建 timeout。

### 1.2 默认 `CreateHost()` 不再在测试里显式重申 `TimeProvider.System`

之前测试辅助方法是：

- `CreateHost(TimeProvider.System, ...)`

现在默认 `CreateHost()` 改成直接走 builder 的默认路径，只在需要 manual time 的测试里才显式传入 `TimeProvider`。

这一步的意义不是“系统时间不能用”，而是：

- 默认系统时间仍然是当前阶段的正常默认值
- 但是否使用默认时间源，应该由 runtime/builder 默认配置决定
- 测试只在需要验证受控时间语义时，才显式注入自定义 `TimeProvider`

## 2. 为什么这一步值得补

### 2.1 helper 只有被调用方和测试同时采用，才算真正收口

前面已经给 host 加了 `CreateTimeoutSource(...)`，但如果测试自己继续直接 new `CancellationTokenSource(...)`，那说明：

- host helper 已经存在
- 但调用侧并没有真正统一到这一套入口

这一轮的价值就在这里：让 invocation timeout tests 也进入同一条超时 API 边界。

### 2.2 “默认时间源”不该在测试里重复宣告

把 `TimeProvider.System` 明着写在默认 helper 里，本质上是让测试重复声明了一遍 runtime 默认值。

这会带来一个轻微但真实的问题：

- runtime 默认策略如果未来调整，测试 helper 也会变成一处额外同步点

当前阶段更合理的边界是：

- 默认 host 构造走 builder 默认配置
- 需要 deterministic time 的测试再显式注入 provider

这样测试表达的就是“我是在验证默认路径”还是“我是在验证受控时间路径”，而不是把两者混在一起。

## 3. 当前推进到哪一层了

如果把最近几轮连起来看，当前时间收口已经逐步覆盖：

1. runtime/control path 自身的时间语义
2. host orchestration helper
3. demo 与 runtime tests 的等待/观察逻辑
4. handoff capture 的 deterministic timestamp verification
5. invocation timeout tests 对 timeout helper 和默认时间源边界的统一

第五层虽然是测试层收尾，但它很重要，因为它消掉了测试自己残留的那条 timeout/wall-clock 分叉。

## 4. 当前阶段还不能说成什么

这轮现在做到的是：

- invocation timeout tests 不再直接 new timeout source
- 默认 host helper 不再在测试里重复写死 `TimeProvider.System`

这轮还没有做到的是：

1. 整个测试工程已经形成完整统一的 host/test fixture
2. 所有测试 helper 的默认配置都已经完全抽象成共享 harness
3. 所有未来时间相关测试都已经有统一 DSL 或更高层断言框架

所以更准确的说法不是：

- “测试层时间模型已经彻底完成”

而是：

- “invocation timeout tests 已经补齐到 host timeout helper 这条统一边界上”

## 5. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不仅在 runtime/host 层提供统一的 provider-aware timeout API，也开始让 invocation timeout tests 自己完全走这条入口；但这仍然只是测试 harness 收口过程中的一个阶段性补丁。`
