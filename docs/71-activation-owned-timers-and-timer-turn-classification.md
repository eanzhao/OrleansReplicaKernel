# Activation-Owned Timer 与 Timer Turn 分类（第七十一篇）

这一篇接在 `70` 后面，开始把 grain 内部的“主动调度源”接进 runtime。

前面几篇里，进入 activation 的 turn 基本都来自请求链：

- 普通 grain 调用
- callback / observer 回跳
- request-chain reentrancy

但 Orleans 里还有另一类完全不同的入口：

- timer
- reminder
- system target 驱动的内部消息

这几类东西的难点在于，它们不是“外面有人发来一个 request”，而是 runtime 自己要往 activation 里塞 turn。

所以这一轮先做最合适的第一刀：

- activation-owned local timer

而且我把边界收得很明确：

- 先只做 activation-owned timer
- 先不做 reminder
- 先不做 system target
- 先不碰持久恢复

## 1. 这轮到底补了什么

这次补的是一条完整但收得很窄的 timer 主链：

1. grain 在 activation 内注册 timer
2. timer 生命周期挂在 activation 上
3. timer 到期后，不是直接在线程池里执行 grain 逻辑
4. 而是重新进入 activation scheduler，变成一个正规的 timer turn
5. activation deactive 时，挂在它上的 timer 会一起取消

这一轮对应新增的核心类型是：

- `IActivationTimerRegistry`
- `IActivationTimerHandle`
- `ActivationTimerRegistration`

以及 `ActivationExecutionContext.CurrentTimerRegistry` 这条上下文入口。

## 2. 为什么 timer 不能直接在线程池里跑

因为如果 timer callback 绕开 scheduler，整个 activation 模型就立刻脏掉了。

你会一下子失去这些保证：

- activation 内部的顺序语义
- 当前 turn 与 timer callback 的先后关系
- quiescing / drain 的可解释性
- deactivation 时的资源收拢边界

所以这轮最关键的实现判断是：

timer callback 必须重新进 activation scheduler，而不是偷偷直接执行。

## 3. 当前 timer callback 的调度分类

这一轮我故意把 timer callback 的分类收得很保守：

- timer callback 默认是 `exclusive turn`
- 每次 timer fire 都会生成新的 `RequestChainId`
- 它不会天然继承当前正在执行请求的 request chain

这意味着：

- timer callback 不会因为当前 activation 里正在跑一个独占请求，就强行插队
- 它会排在当前独占 turn 后面
- 它也不会误吃到 request-chain reentrancy 的特权

这个选择是刻意的。

因为 timer 本质上不是“当前请求链打回来的 nested request”，它是 activation 自己养出来的一次新事件源。把它硬塞进当前 request chain，只会把 reentrancy 语义弄脏。

## 4. activation-owned 到底是什么意思

这轮的 timer 不是全局 timer，也不是 host 级 timer，而是：

- 注册动作发生在 activation execution context 里
- timer handle 存在 activation entry 里
- activation dispose 时，这批 timer 会统一取消

所以它现在有两个很重要的性质：

### 4.1 timer 跟 activation 走

activation 还活着，timer 才有机会继续 fire。

### 4.2 deactivation 会把 timer 一起带走

activation 下线后，已经注册但还没 fire 的 timer，不应该再偷偷执行。

这一点很关键，因为这是 timer 生命周期和 activation 生命周期真正绑定在一起的地方。

## 5. demo 里怎么验证

这轮我在 `EchoGrain` 里加了两条专门用来观察 timer 语义的方法：

- `HoldTurnWithTimerAsync(...)`
- `ArmOneShotTimerAsync(...)`

再加一个查看结果的方法：

- `GetTimerSnapshotAsync()`

### 5.1 `timer-hold`

这条链做的事情是：

1. 先进入一个独占 grain turn
2. turn 内注册一个 one-shot timer
3. timer 会在当前 turn 还没结束时到期
4. 但它不会插队
5. 必须等当前 turn 结束，才作为一个新的 timer turn 进入 activation

所以它会得到非常有代表性的结果：

- `HoldTurnWithTimerAsync(...)` 返回：`hold:during-hold:count=1`
- 后面再看 timer snapshot：`timer:during-hold:count=2`

如果 timer callback 是直接在线程池里跑，或者错误地绕过了 activation barrier，这个返回值就不会这么干净。

### 5.2 `timer-deactivate`

这条链做的事情是：

1. 给 activation 挂一个 one-shot timer
2. 在它触发前立刻 deactive grain
3. 再等超过原来的 due time
4. 最后看新 activation 的 timer snapshot

结果应该是：

- `<none>`

这就说明 old activation 上挂着的 timer 已经跟着 deactivation 一起停掉了，没有在后台偷偷再 fire 一次。

## 6. 这轮真正解决了什么问题

这轮真正解决的，不是“grain 终于能注册 timer 了”这么简单。

更重要的是，runtime 现在第一次明确区分开了两类 activation turn：

- 从 request / callback 链来的 turn
- 从 activation 内部 timer 源触发的 turn

而且 timer 这条线已经有了自己的规则：

- 新 chain
- 独占
- 跟 activation 生命周期绑定

这件事一旦做对，后面 reminder、system target、内部 runtime task 这些东西，才有地方继续分类。

## 7. 这还不等于 Orleans 的 timer/reminder 模型已经完成

这里也要讲清楚。

这一轮现在有的是：

- activation-owned local timer
- timer callback 作为正规 activation turn
- timer 跟 activation 生命周期绑定

这一轮还没有的是：

- reminder
- 持久 timer / reminder 恢复
- timer persistence
- timer 与 cluster failover 的关系
- timer / reminder / system target 的更细粒度调度分类
- Orleans 里那套更完整的定时能力模型

所以这轮更准确的定位是：

- 我们先把“activation 自己养的 timer”接进了 runtime
- 但还没有开始做“跨 activation 生命周期存在的 reminder”

## 8. 为什么这轮对后面继续复刻 Orleans 很重要

因为从 runtime 的角度看，timer/reminder 这类能力最难的不是“定时器 API 怎么设计”，而是：

- 这些定时事件进入 activation 时，到底算什么 turn
- 它们跟当前 activation 的独占 / interleaving / reentrancy 关系是什么
- activation deactive 时，runtime 到底该怎么收口

这一轮把第一层答案给出来了：

- timer callback 是独占 turn
- 它是新的 chain
- 它跟 activation 生命周期强绑定

这套判断虽然保守，但边界很干净。后面真要继续往 Orleans 靠：

- reminder
- timer recovery
- system target scheduling
- timer callback 与 interleaving/reentrancy 的组合

都能在这条干净边界上往前推，而不是回头重做一遍 timer 入口。
