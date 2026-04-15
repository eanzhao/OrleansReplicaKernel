# 84. Handoff Capture 测试采用受控时间

这一篇接在前面几轮 testing/time convergence 后面，补的是 routing/handoff 这条测试线里的最后一块明显 wall-clock 依赖。

前几轮已经逐步做到：

- activation-local lifecycle time 走 `TimeProvider`
- transport / retry / grain body / host orchestration 都开始共享统一时间源
- demo 和 runtime tests 里的很多 observation wait 开始脱离固定睡眠

但 `LocalActivationDirectoryTests` 里还留着一个比较突兀的缺口：

- staged handoff record 直接吃 `DateTimeOffset.UtcNow`
- restore 测试继续显式传 `TimeProvider.System`

这意味着 handoff 测试虽然已经在验证 owner version、handoff apply、idle collect 等行为，却还没有把：

- handoff capture timestamp 自己

收进受控时间故事里。

所以这一轮补的是：

`让 LocalActivationDirectoryTests 也开始共享 deterministic time，并直接验证 warm handoff capture 的 CapturedUtc 语义。`

## 1. 当前这轮具体做了什么

### 1.1 staged handoff tests 不再直接读 wall clock

原来两条 staged handoff 测试在构造 `ActivationHandoffRecord` 时，直接使用：

- `DateTimeOffset.UtcNow`

现在改成了：

- 统一的 `ManualTimeProvider`
- 显式构造的 handoff record timestamp

这让测试里的 handoff state 不再夹带一份不受控的系统时钟。

### 1.2 restore 路径也开始显式使用受控时间

`Restore_RejectsRecoveredStaleRequestsAndAllowsCurrentOwnerVersion` 之前继续把 restore directory 挂在 `TimeProvider.System` 上。

现在 restore 也改成走 `ManualTimeProvider`，所以这条测试不再在 restore 分支偷偷掉回系统时钟。

### 1.3 新增 `PrepareHandoffAsync_UsesConfiguredTimeProviderForCapturedUtc`

这一轮最值钱的新增，不是把 `UtcNow` 替掉，而是补了一条直接验证：

1. activation 在受控时间下被创建
2. provider 时间推进到某个时刻
3. `PrepareHandoffAsync(...)` 触发 capture
4. 返回的 `ActivationHandoffRecord.CapturedUtc` 就是当前 provider 时间

这条测试真正证明了：

- warm handoff capture timestamp 本身

也已经纳入统一时间语义，而不是只是周边测试用了 manual time。

## 2. 为什么这一步值得单独推进

### 2.1 handoff 不是边角路径

对这个项目来说，warm handoff 不是一个 demo trick，而是复刻 Orleans 时必须成立的一条主链：

- 旧 owner drain
- capture state
- staged transfer
- 新 owner apply
- stale request fencing

既然前面已经在 activation/runtime/host 层逐步收时间边界，那么 handoff capture 这条路径不应该长期留着系统时钟盲点。

### 2.2 `CapturedUtc` 也是语义的一部分

`ActivationHandoffRecord` 里不是只有 payload，还有：

- `CapturedUtc`

这意味着 capture timestamp 不是日志噪音，而是 handoff record 本身携带的运行时语义。

既然如此，就值得有一条测试直接证明它跟着配置的 `TimeProvider` 走。

## 3. 当前推进到哪一层了

如果把前面几轮和这一轮连起来看，统一时间故事现在已经开始覆盖：

1. activation / runtime / transport / retry
2. grain body / host orchestration
3. demo / runtime tests 的 observation 语义
4. handoff capture timestamp 自身的 deterministic verification

第四层很关键，因为它把“迁移状态是什么时候被抓取的”也纳入了同一条时间边界。

## 4. 当前阶段还不能说成什么

这轮现在做到的是：

- `LocalActivationDirectoryTests` 不再把 handoff timestamp 挂在 wall clock 上
- warm handoff capture 的 `CapturedUtc` 已经有了直接的受控验证

这轮还没有做到的是：

1. 所有 handoff 相关测试都已经完全改造为 provider-aware harness
2. 更高层 rebalancing / host handoff flows 的所有时间断言都已统一
3. handoff telemetry / tracing 的时间模型已经完全定型

所以更准确的说法不是：

- “handoff 时间模型已经全部完成”

而是：

- “warm handoff capture timestamp 已经开始拥有 deterministic test coverage”

## 5. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不仅在 activation/runtime/host 层统一时间语义，也开始让 LocalActivationDirectory 的 warm handoff capture timestamp 接受同一套受控时间验证；但这仍然只是 handoff 时间边界收口的一部分。`
