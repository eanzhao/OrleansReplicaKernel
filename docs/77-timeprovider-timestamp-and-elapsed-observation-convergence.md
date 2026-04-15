# 77. TimeProvider Timestamp 与 Elapsed 观测收敛

这一篇接在 `76` 后面，补的是统一时间源里一个容易被忽略、但实际上很关键的缺口：

- delay / timeout 已经逐步吃 `TimeProvider`
- 但 elapsed measurement 如果还在直接用 `Stopwatch`，观测侧的时间语义仍然会裂开

前几轮已经把很多“真正控制行为的时间”收进了同一个 provider：

- activation timer
- idle collect
- membership view change timestamp
- transport delay
- retry backoff
- grain slow turn
- host/demo caller timeout

但如果 elapsed 观测还在裸吃 `Stopwatch`，那就会出现一种很别扭的状态：

- 系统行为本身跟着 manual time 走
- 你记录出来的 elapsed 却还是 wall clock

所以这一轮补的是：

`让 TimeProvider 的 timestamp / elapsed 观测语义，也开始和前面的 delay / timeout 收敛到同一条时间线上。`

## 1. 当前这轮具体做了什么

### 1.1 `ManualTimeProvider` 不再只会给 `UtcNow`

之前测试里的 `ManualTimeProvider` 已经能做两件事：

- 控制 `GetUtcNow()`
- 驱动 provider-based timer

但它还缺一块：

- `GetTimestamp()`
- `TimestampFrequency`

这意味着只要代码里出现基于 timestamp 的 elapsed measurement，这份 manual time 就会掉回默认实现，重新碰到真实的高精度时钟。

这一轮补上后，`ManualTimeProvider` 现在不仅能控制“当前时间”和“timer 触发”，也能给出和自己推进节奏一致的 timestamp。

### 1.2 demo 里的 elapsed 观测不再直接依赖 `Stopwatch`

这轮把 `Program` 里 interleaving 演示的 elapsed 记录从：

- `Stopwatch.StartNew()`

改成了：

- `host.TimeProvider.GetTimestamp()`
- `host.TimeProvider.GetElapsedTime(...)`

这件事的意义不是“为了省掉一个 `using System.Diagnostics`”，而是让：

- demo 里的等待
- demo 里的 timeout
- demo 里的 elapsed 观测

第一次都能站到同一套时间源上。

## 2. 为什么这一步值得单独补

如果这里不补，manual-time story 其实还是会留一个观测层断点。

### 2.1 行为时间和观测时间会分裂

现在很多行为已经吃 provider：

- turn 什么时候结束
- timeout 什么时候触发
- timer 什么时候 due

如果 elapsed 还吃 `Stopwatch`，你最后看到的“用了多久”就不是同一套时间。

这会导致一种很难解释的情况：

- 行为上看像是 150ms
- 观测上看却像是几毫秒或几百毫秒 wall clock

### 2.2 deterministic test / demo 解释会不够闭环

当前项目不是只想做“某些点可控”的时间验证，而是要逐步建立一套：

- 行为可控
- 观测可控
- 文档表述也一致

的时间边界。

timestamp / elapsed 不收进来，这个闭环就还没补完。

## 3. 这轮新增的验证

这一轮新增了一条 `ManualTimeProvider` 测试，直接验证：

1. `GetTimestamp()` 会随着手工推进的时间前进
2. `TimestampFrequency` 是稳定的
3. `GetElapsedTime(...)` 返回的结果和 `Advance(...)` 的步长一致

这条测试的价值在于，它把 `ManualTimeProvider` 从“能控当前时刻的 test double”推进成了“能同时支撑 delay、timer、elapsed measurement 的 test clock”。

## 4. 当前推进到哪一层了

如果把 `72` 到 `77` 连起来看，统一时间源现在已经逐步覆盖了：

1. activation-local lifecycle
2. membership timestamp
3. transport / retry control path
4. grain execution body
5. host/demo timeout 与 delay
6. timestamp / elapsed 观测

第六层很重要，因为它开始把“系统怎么跑”和“我们怎么量这个系统”对齐到一套时钟上。

## 5. 当前阶段还不能说成什么

这轮做到的不是：

- 整个应用层 timing API 已经定型
- 所有性能/benchmark 统计都已经切到 provider-aware 模型
- 所有测试都已经完全摆脱真实等待

这轮做到的是：

- manual time 现在已经能为 elapsed measurement 提供一致语义
- demo 层至少有一条真实的 elapsed 观测开始不再依赖裸 `Stopwatch`

所以更准确的描述不是：

- “所有计时体系都已经完成统一”

而是：

- “统一时间源开始同时覆盖控制路径和观测路径”

## 6. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不仅让 delay、timeout 和 timer 共享 TimeProvider，也开始让 elapsed/timestamp 观测共享这条时间线；但这仍然只是统一时间模型的阶段性推进，不是最终的应用层 timing API。`
