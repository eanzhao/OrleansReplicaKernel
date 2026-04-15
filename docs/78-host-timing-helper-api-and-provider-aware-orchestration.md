# 78. Host Timing Helper API 与 Provider-Aware Orchestration

这一篇接在 `76` 和 `77` 后面，补的是 host 侧时间模型的下一步收口。

前两轮已经做到：

- host 能暴露当前 `TimeProvider`
- demo 里的 delay / caller timeout / elapsed measurement 开始吃同一时间源

但如果一直停在“把 `TimeProvider` 露出来，然后让每个调用方自己手搓 `Task.Delay(...)`、`new CancellationTokenSource(...)`、`GetTimestamp()`”，那 host 侧的时间模型其实还是有一个明显问题：

- 语义是统一了
- 用法却还是分散的

也就是说，当前调用方虽然能拿到正确时间源，但每个地方都还得自己记住：

- provider-aware delay 怎么写
- provider-aware timeout source 怎么建
- elapsed measurement 应该用哪条 timestamp

这轮补的，就是把这几件事收成 host 自己的阶段性 helper API。

## 1. 当前这轮具体加了什么

`OrleansReplicaKernelHost` 现在开始提供这几类 helper：

- `DelayAsync(...)`
- `CreateTimeoutSource(...)`
- `GetTimestamp()`
- `GetElapsedTime(...)`

它们本质上还是以 host 当前的 `TimeProvider` 为底层时间源，但现在调用方不再需要每次自己重新拼那套样板。

## 2. 为什么这一步值得单独做

### 2.1 时间语义统一，不代表 API 已经收口

如果只是把 `TimeProvider` 裸暴露给外面，那么每个调用方都可能写出自己的变体：

- 有的地方记得用 provider-aware `Task.Delay`
- 有的地方继续写成 wall clock timeout
- 有的地方 timestamp/elapsed 写法不一致

这样虽然底层能力在，但上层调用模式还是容易重新散掉。

### 2.2 host orchestration 是一层真实边界

当前项目的 host 不是无意义的薄壳，它承载的就是：

- demo orchestration
- 运行时演示入口
- 未来更完整宿主/管理面能力的阶段性边界

既然调用侧时间现在已经推进到 host 这层，那么把 delay / timeout / elapsed helper 正式收进 host，是更干净的边界落点。

### 2.3 这让后续代码不必继续复制时间接线样板

现在 `Program` 已经不需要再维护本地的：

- `PauseAsync(...)`
- `CreateTimeout(...)`

而是直接用 host helper。

这件事本身不大，但它说明 host 侧时间 API 已经开始从“裸 provider 暴露”走向“有边界的调用辅助”。

## 3. 当前这轮的验证

这一轮新增了一条 manual-time 测试，直接验证 host helper 的三件事：

1. `DelayAsync(...)` 会跟着 manual time 推进完成
2. `CreateTimeoutSource(...)` 的取消时机跟着同一时间源推进
3. `GetTimestamp()/GetElapsedTime(...)` 得出的 elapsed 结果也和同一套 manual time 一致

这条测试的价值在于，它不是只测某个底层 `TimeProvider` 能力，而是直接钉住：

- host 暴露出来的 orchestration API 本身已经共享同一套时间语义

## 4. 这轮真正推进的边界

如果把 `76`、`77`、`78` 连起来看，host 侧时间模型已经推进了三层：

1. host 可以暴露当前 `TimeProvider`
2. host/demo 侧的 delay / timeout / elapsed 观测开始共享这条时间线
3. host 开始提供显式的 provider-aware orchestration helper

第三层很关键，因为它开始把“统一时间源”从一种配置能力，推进成一种 host 侧的显式用法约束。

## 5. 当前阶段还不能说成什么

这里也要讲清楚。

这轮现在做到的是：

- host 提供了阶段性的 provider-aware orchestration helper
- demo 和测试开始走这套 helper

这轮还没有做到的是：

1. 宿主层 timing API 已经完全定型
2. 外部 client / gateway / management plane 都已经统一接入这套 helper
3. 更高层应用代码一定会只通过 host helper 访问时间

所以更准确的描述不是：

- “最终宿主时间 API 已经设计完成”

而是：

- “host 已经开始形成显式的 provider-aware timing helper 边界”

## 6. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不只是在 host 上暴露 TimeProvider，也开始把 delay、timeout 和 elapsed measurement 收成 host 自己的 provider-aware helper；但这仍然只是阶段性宿主 API，而不是最终形态。`
