# 82. 共享测试同步 Helper 与 Manual-Time Progress

这一篇接在 `81` 后面，继续收测试层的同步语义，但重点从“某一组测试去掉固定睡眠”推进到“开始形成共享的 test harness helper”。

前一轮已经把 `ActivationSchedulerTests` 里几处显眼的：

- `Task.Delay(100)`

改成了基于 dispatch opportunity 的断言。

但测试层还留着另一类更细碎的等待：

- manual-time 测试里到处散着 `Task.Delay(20)`

这些等待的真实目的并不是：

- “真的等 20ms”

而是：

- “给 continuation / callback / transport retry / timer callback 一个 dispatch 和 progress 的机会”

如果继续让这些等待散落在各个测试里，问题有两个：

1. 语义不清楚
2. 每个测试都在自己猜一个小睡眠窗口

所以这一轮补的是：

`把这种 dispatch/progress 同步收成共享的 test helper，而不是继续让它散落成很多个局部 Task.Delay(20)。`

## 1. 当前这轮具体做了什么

### 1.1 新增共享的 `AsyncTestSync.YieldUntilDispatchAsync(...)`

测试支持层现在新增了一个共享 helper：

- `AsyncTestSync.YieldUntilDispatchAsync(...)`

它做的事情不是“等待某个业务语义成立”，而是更底层的：

- 给异步 continuation 若干轮 `Task.Yield()`
- 再补一个极小的 dispatch backoff

也就是说，这个 helper 解决的是：

- “异步链路应该已经有机会推进了”

而不是：

- “某个具体状态一定已经变成真了”

### 1.2 `ActivationSchedulerTests` 和 manual-time invocation tests 开始共享它

这一轮最直接的改动，是把两类原来分散的同步点收进同一个 helper：

- scheduler 顺序测试里的 dispatch opportunity 断言
- manual-time 的 invocation/transport/timer tests 里的 progress wait

这样测试层第一次开始把：

- dispatch synchronization
- continuation progress synchronization

都落到同一个 test harness helper 上。

## 2. 为什么不是继续写“更多局部 Task.Delay(20)”

### 2.1 这些等待表达的是调度机会，不是时长

manual-time 测试里那些 `20ms` 的真实语义并不是“这个业务需要 20ms”，而是：

- provider 时间推进之后
- runtime 还需要一个异步调度机会

如果继续把它写成局部睡眠，读测试的人就很难看出：

- 这里到底是在等业务时间
- 还是在给 continuation 一次落地机会

共享 helper 至少把这个语义显式化了。

### 2.2 纯 `Task.Yield()` 不够，纯固定睡眠也不理想

这轮实现里也验证了一个实际问题：

- 纯 `Task.Yield()` 对某些 transport/retry continuation 不够稳
- 继续回到 `Task.Delay(20)` 又太粗

所以当前阶段更实际的收口方式是：

- 用共享 helper 做“若干轮 yield + 极小 backoff”

这比散落的固定 20ms 窗口更贴近这里真正要表达的同步语义。

## 3. 这一轮真正推进的边界

如果把 `81` 和 `82` 连起来看，测试层的同步收口开始形成两层：

1. 去掉几处显眼的固定睡眠
2. 把剩下那类 dispatch/progress 等待收进共享 helper

第二层更关键，因为它意味着测试同步不再只是“局部重写了一两处”，而是开始形成统一的 harness 习惯。

## 4. 当前阶段还不能说成什么

这里还是要区分清楚。

这轮做到的是：

- scheduler 与 manual-time tests 开始共享 dispatch/progress helper
- 很多散落的 `Task.Delay(20)` 已经被替换掉

这轮还没有做到的是：

1. 所有测试等待都已经彻底统一到一套 harness
2. 所有真实时间 timeout 都已经消失
3. 所有测试都已经完全 provider-aware 且不再需要任何微小 backoff

所以更准确的说法不是：

- “测试层已经完全无真实时间等待”

而是：

- “测试层开始形成共享的 dispatch/progress 同步 helper，逐步替代散落的小睡眠窗口”

## 5. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不只是在零散测试里去掉固定睡眠，也开始把 dispatch/progress 同步收进共享的 test harness helper；但这仍然只是测试同步统一化的阶段性推进。`
