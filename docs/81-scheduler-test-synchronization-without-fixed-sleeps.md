# 81. Scheduler Test Synchronization Without Fixed Sleeps

这一篇不是在改调度器本身，而是在继续收测试侧的等待语义。

前几轮已经把 runtime、grain、host、demo orchestration 里的很多时间边界逐步收进了统一 `TimeProvider`，也开始把固定 observation sleep 换成语义化等待。

但测试侧还留着一类比较粗的验证方式：

- 先 enqueue 一个不该开始的 turn
- 固定 `Task.Delay(100)`
- 再断言它还没开始

这种写法的问题不在于它一定错误，而在于它把验证建立在：

- “睡 100ms 应该足够了”

而不是：

- “已经给过调度器足够的 dispatch 机会，但条件仍然不成立”

所以这一轮补的是 scheduler 验证层面的同步方式收口。

## 1. 当前这轮具体做了什么

`ActivationSchedulerTests` 里原来那几处：

- `Task.Delay(100)`

现在都改成了一个小的信号化 helper：

- 连续 `Task.Yield()` 若干轮

然后再断言：

- 某个不该启动的 turn 仍然没有启动

换句话说，测试不再依赖固定睡眠窗口去“赌调度器应该已经有机会犯错”，而是显式给它 dispatch 机会，再做断言。

## 2. 为什么这比固定睡眠更合适

### 2.1 它验证的是调度语义，不是墙钟时长

这些测试真正想证明的是：

- exclusive turn 会挡住后续 exclusive/interleavable turn
- later interleavable 不会绕过已排队的 exclusive
- 不同 request chain 不能重入 active exclusive turn

这里的关键不是“100ms 之后会怎样”，而是“在调度器已经有机会处理队列之后，会不会错误启动”。

### 2.2 这能减少 sleep-driven test flakiness

固定睡眠本质上还是 heuristic。

睡太短：

- 测试可能偶发失败

睡太长：

- 测试会变慢

而 `Task.Yield()` 驱动的同步方式更接近这里真正想表达的验证语义：

- 给异步 dispatch 足够机会
- 不把行为和某个 wall-clock 窗口硬绑在一起

## 3. 这轮没有把它说成“所有测试都完全 deterministic 了”

这里要讲清楚，这轮现在做到的是：

- scheduler 这组 ordering tests 不再依赖固定 `Task.Delay(100)`

这轮还没有做到的是：

1. 所有测试等待都已经彻底摆脱真实时间
2. 所有 `WaitAsync(TimeSpan.FromSeconds(1))` 都已经被替换
3. 所有验证路径都已经收敛到同一套 provider-aware test harness

所以更准确的描述不是：

- “测试层已经完全无睡眠”

而是：

- “scheduler 的关键顺序断言已经开始脱离固定睡眠窗口”

## 4. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不仅在 runtime/host/demo 层逐步消除固定睡眠，连 ActivationScheduler 的关键顺序测试也开始改成基于 dispatch opportunity 的信号式断言；但这仍然只是测试同步语义收口的一部分。`
