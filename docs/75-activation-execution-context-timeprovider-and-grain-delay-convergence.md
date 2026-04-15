# 75. ActivationExecutionContext TimeProvider 与 Grain Delay 收敛

这一篇接在 `72` 和 `74` 后面，继续把时间语义从 runtime 边界往 grain 执行体内部推进。

前面两轮已经把很多 runtime 自己控制的时间线收进了统一的 `TimeProvider`：

- activation-local lifecycle time
- transport 注入延迟
- retry backoff
- membership history 时间戳

但如果这时候停下来，demo grain 这边其实还留着一个很显眼的断层：

- `EchoGrain.PingSlowAsync(...)`
- `EchoGrain.HoldTurnWithTimerAsync(...)`

这两条方法内部都还在直接调用：

- `Task.Delay(delayMs, cancellationToken)`

这意味着：

- runtime 外壳的时间已经能被 provider 控制
- grain 自己那段“慢调用/持有 turn”的执行时间却还在吃系统时钟

所以当前这篇补的，不是“再多改几个 `Task.Delay`”，而是把：

`activation execution context 里的当前时间源，正式带给 grain 执行代码。`

## 1. 为什么这里不能一直停在“runtime 统一了，grain 自己随便”

如果这块一直不补，问题不会只停在 demo 代码有点粗糙。

### 1.1 activation turn 的时间语义会断在 grain 代码里

现在 activation 已经能保证：

- timer due time 吃 provider
- idle collect 吃 provider
- retry backoff 吃 provider

但一旦真正进入 grain 方法体，如果 grain 自己的慢路径还是直接 `Task.Delay(...)`，同一个 turn 的时间语义就会裂开：

- turn 外层是 provider time
- turn 内层又掉回 wall clock

这对“activation time boundary 已经统一”这个判断是有破坏性的。

### 1.2 manual-time 测试只能测到 runtime 外壳，测不到 grain 执行体

如果 grain 代码里的慢路径还是系统时钟，那你现在虽然可以测：

- transport delay
- retry delay
- timer fire

但你还没法精确测：

- 一个 slow grain turn 什么时候结束
- 一个 hold turn 在 timer due 之后为什么还没放行

这就让 deterministic test story 还是少了一块。

### 1.3 timer ordering 的解释会不够干净

`HoldTurnWithTimerAsync(...)` 这条链最想证明的是：

- timer 在当前 turn 还没结束时就已经 due
- 但它不会绕过当前 activation barrier

如果 hold delay 自己还是 wall clock，那这里的解释就会显得半真半假：

- timer due 是 provider time
- hold turn release 却是系统时钟

这会让“它为什么现在还没结束”这件事变得不够纯。

## 2. 当前这版具体做了什么

这轮做的事情很小，但边界很关键。

### 2.1 `ActivationExecutionContext` 现在暴露 `CurrentTimeProvider`

之前 activation execution context 里已经有：

- `CurrentRuntime`
- `CurrentRequestChainId`
- `CurrentGrainId`
- `CurrentTimerRegistry`

这一轮再补上一条：

- `CurrentTimeProvider`

而且这不是全局静态时钟，而是：

- activation 在真正执行 turn 时，由 runtime 把当前 time provider 一起放进 execution context

也就是说，现在 grain 方法在 activation 内执行时，第一次拿得到“这次执行所属 runtime 的当前时间源”。

### 2.2 `ActivationEntry` 在 request turn 和 timer turn 两条路径都把 provider 带进 context

这一点必须两边都接。

否则会出现：

- 普通请求 turn 能拿到 provider
- timer callback turn 又拿不到

当前这版在两条入口都把 `_timeProvider` 一起传进了 `ActivationExecutionContext.RunAsync(...)`。

所以 execution context 不会因为 turn 来源不同而掉链子。

### 2.3 `EchoGrain` 的慢路径不再裸吃 wall clock

现在 `EchoGrain` 里的这两条慢路径：

- `PingSlowAsync(...)`
- `HoldTurnWithTimerAsync(...)`

都改成先走一个统一的 `DelayAsync(...)`，逻辑是：

- activation 内有 `CurrentTimeProvider` 时，就按 provider delay
- 没有时再退回原来的 `Task.Delay(...)`

这个 fallback 也很重要，因为它保证了：

- activation 内执行时能吃统一时间源
- 离开 activation context 的情况下也不会直接炸掉

## 3. 这轮最值钱的验证

这轮最值钱的不是“EchoGrain 多了个 helper”。

真正值钱的是两条新的 manual-time 测试：

### 3.1 `PingSlowAsync(...)` 可以只靠推进 provider 结束

现在可以精确验证：

1. 发起一个 `80ms` 的 slow call
2. 不推进 provider，不会完成
3. 推进 `79ms`，还不会完成
4. 再推进 `1ms`，slow turn 才结束

这说明 grain 自己的 slow turn 时间，已经不再独立漂在系统时钟上。

### 3.2 `HoldTurnWithTimerAsync(...)` 现在能在同一时间源上同时验证两件事

现在还可以在一套 provider 上同时验证：

1. hold delay 自己什么时候结束
2. timer due 之后为什么还不能绕过当前 turn

也就是说：

- timer due 是 provider time
- hold turn release 也是 provider time

这样“timer 不会 bypass 当前 turn”这件事就终于能在同一套时间语义里被验证，而不是半边靠 provider、半边靠真实等待。

## 4. 这轮真正推进了哪条边界

如果把 `72`、`74`、`75` 连起来看，会发现时间语义已经从“runtime 自己的边界”继续往里推了一层：

1. activation-local lifecycle time
2. request/response control-path time
3. activation 内 grain 执行体可见的当前时间源

第三层很关键，因为它第一次让 grain 方法体本身，能够感知“当前运行时到底在用哪套时间”。

这件事一旦建立，后面很多东西才有清晰落点：

- provider-aware grain 测试
- 更可控的 demo 行为
- reminder/system-target 这类更内部的 turn source
- 更正式的 activation-scoped runtime services

## 5. 当前故意还没说成“所有 grain 代码都 provider-aware 了”

这里也要讲清楚。

这轮现在做到的是：

- execution context 开始暴露当前 `TimeProvider`
- request turn / timer turn 两条 activation 执行路径都能带出这条信息
- `EchoGrain` 的慢调用和 hold-delay 路径开始吃统一时间源

这轮还没有做到的是：

1. 所有 demo grain 都统一成 provider-aware
2. 用户 grain 的 delay/clock 访问模型已经定型
3. provider-aware grain service 形成正式 API
4. 整个应用层都不再直接碰 wall clock

所以更准确的定位不是：

- “grain 层时间模型已经完成”

而是：

- “activation execution context 已经开始把统一时间源带进 grain 执行体”

## 6. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的说法是：

`OrleansReplicaKernel 已经开始把统一的 TimeProvider 从 activation runtime 边界继续带进 grain 执行体：ActivationExecutionContext 现在暴露当前时间源，EchoGrain 的 slow-turn 和 hold-delay 路径也开始共享这套时间语义；但这还不是应用层时间访问模型的最终形态。`
