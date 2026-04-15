# 76. Host TimeProvider、Caller Timeout 与 Demo Delay 收敛

这一篇接在 `74` 和 `75` 后面，继续把统一时间源往 runtime 外面再推一层。

前面几轮已经做到：

- activation-local lifecycle time 走 `TimeProvider`
- transport 注入延迟和 retry backoff 走 `TimeProvider`
- grain 执行体内部能通过 `ActivationExecutionContext` 看到当前 `TimeProvider`

但如果这时候停下来，外层还是会留一个断层：

- host/demo 代码里的等待还在直接 `Task.Delay(...)`
- caller timeout 还在直接 `new CancellationTokenSource(timeout)`

这意味着：

- runtime 和 grain body 已经可以吃受控时间
- 真正发起调用的应用侧等待和 timeout 语义却还在 wall clock 上

所以这一轮补的，是 `TimeProvider` 从 runtime/grain 边界继续往 host/demo 调用侧推进。

## 1. 当前这轮具体改了什么

### 1.1 `OrleansReplicaKernelHost` 现在显式暴露当前 `TimeProvider`

builder 里本来就已经有配置好的 `TimeProvider`，只是之前只在 runtime 内部消费。

这一轮开始，host 也会保留并暴露这条时间源：

- runtime 继续吃它
- app/demo 侧现在也能拿同一份 provider

这一步的意义不是“host 从此承担完整的应用时钟 API”，而是先把统一时间源正式带出 runtime 外壳。

### 1.2 `Program` 里的演示等待改成走 host 当前时间源

当前 `Program` 里原来那批裸 `Task.Delay(...)`，现在都改成统一走：

- `PauseAsync(...)`

而这层 helper 最终使用的是：

- `Task.Delay(delay, host.TimeProvider, ...)`

所以现在 demo 里这些等待：

- stale retry 前的短暂停顿
- late response / stale replay / duplicate replay 的观察窗口
- timer hold / deactivate 观察窗口
- membership stabilization 等待
- interleaving / drain / idle collect 相关等待

都开始共享 host 当前时间源。

### 1.3 caller timeout 也开始走同一条时间线

这轮还把 demo 里 late-response 场景的 timeout 从：

- `new CancellationTokenSource(timeout)`

改成了：

- `new CancellationTokenSource(timeout, host.TimeProvider)`

这件事很关键，因为它让：

- transport delay
- grain slow turn
- caller timeout

第一次可以落在同一套时间语义上观察，而不是前两者吃 provider，最后一个还吃系统时钟。

## 2. 这轮新增的验证

最关键的是新增了一条 manual-time 测试，直接验证：

1. caller timeout 在 provider 推进到 `50ms` 前不会触发
2. provider 再推进 `1ms` 后，调用端会按预期取消
3. grain 里的 slow turn 仍然继续执行到完成
4. late response 最终还是会被调用端按原来的规则丢弃

这说明现在“调用方超时”和“运行时内部时间”已经不再分裂成两套时钟。

## 3. 这轮真正推进的是哪条边界

如果把 `72`、`74`、`75`、`76` 连起来看，统一时间源已经跨过了四层边界：

1. activation-local lifecycle
2. request/response control path
3. grain execution context
4. host/demo 调用侧等待与 caller timeout

第四层的价值在于，它开始把“deterministic runtime test”往“deterministic end-to-end scenario”推进。

## 4. 当前阶段还不能说成什么

这里还是要刻意讲清楚。

这轮现在做到的是：

- host 能暴露当前 `TimeProvider`
- demo orchestration 的等待开始共享这条时间线
- caller timeout 可以显式绑定到同一个 provider

这轮还没有做到的是：

1. OrleansReplicaKernel 已经定义好了最终形态的应用层时钟 API
2. 用户业务代码会自动获得 provider-aware timeout / delay 能力
3. 所有测试等待都已经完全脱离少量真实时间让步
4. 外部 client/gateway 边界已经完成统一时间建模

所以更准确的说法不是：

- “应用层时间模型已经完成”

而是：

- “统一时间源已经开始越过 runtime/grain 边界，进入 host/demo 调用侧”

## 5. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不仅让 runtime 和 grain 执行体共享 TimeProvider，也开始让 host/demo 侧的等待与 caller timeout 共享同一时间源；但这还只是应用层时间模型的阶段性收敛，不是最终 API 形态。`
