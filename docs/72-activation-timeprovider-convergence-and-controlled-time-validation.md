# Activation TimeProvider 收敛与可控时间验证（第七十二篇）

这一篇接在 `58` 和 `71` 后面，补的是一条之前还没有完全收住的边界：

- `58` 已经把 response history retention 接进了 `TimeProvider`
- `71` 已经把 activation-owned timer 这条主链接进了 runtime

但在那之后，activation 本地时间语义其实还是分裂的：

- response history 用的是注入时间源
- activation timer 还在直接吃 wall clock
- activation `LastTouchedUtc` 还在直接读 `UtcNow`
- idle collect 也还在直接读 `UtcNow`

这意味着 runtime 里已经出现了一个很别扭的状态：

`同一个 activation 相关的时间事实，分散在两套时钟上。`

这一篇补的，就是把这条 activation-local time boundary 先收成一条统一时间线。

## 1. 为什么这里不能继续“半边统一、半边裸跑”

如果 timer、idle collect、activation metadata 继续直接吃系统时钟，问题不会只停在“测试不方便”。

它会同时影响三件事：

### 1.1 activation 生命周期解释会变脏

同一个 activation 上会同时出现：

- timer due time 按 wall clock 解释
- `LastTouchedUtc` 按 wall clock 记录
- response retention 按 injected time provider 解释

这会让“这个 activation 现在该不该收”“这个 timer 现在该不该 fire”这类判断，不再处在同一套时间语义里。

### 1.2 可控时间测试会断层

只要 timer 还在用真实时间：

- 你就没法只靠推进 `ManualTimeProvider` 去驱动 timer fire
- 验证 idle collect 时也还得 `Task.Delay(...)`
- “窗口刚过期”和“窗口还没过期”这种断言就会继续依赖真实等待

这对当前阶段还能忍，但对后面继续往 reminder、恢复、更多 runtime 计时逻辑推进，就不够稳了。

### 1.3 callback activation 会落在另一套时间线上

如果普通 activation 改了，但 callback activation 还保留系统时钟，那么：

- grain activation 的本地时间语义是一套
- callback target activation 又是另一套

这会把 observer / callback 这一侧重新变成例外分支。

所以这轮真正要守住的不是“timer 也能吃个 provider”，而是：

`activation 本地时间边界里的几个核心事实，要尽量落在同一时间源上。`

## 2. 当前这版具体补了什么

这一轮补的东西不大，但边界很明确。

### 2.1 `ActivationEntry` 现在持有 `TimeProvider`

activation 创建时就拿到时间源，后面这些事实都改成从 provider 取：

- 初始 `LastTouchedUtc`
- 每次 `Touch()`
- warm handoff capture 时间

这意味着 activation metadata 本身，终于不再偷偷绕回系统时钟。

### 2.2 activation-owned timer 改成按 provider 解释 due/period

`ActivationTimerRegistration` 不再裸用：

- `Task.Delay(dueTime, token)`
- `Task.Delay(period, token)`

而是改成：

- `Task.Delay(delay, timeProvider, token)`

所以 timer fire 的时间推进，现在和 activation 自己的本地时间语义站到了同一侧。

### 2.3 idle collect 改成按同一 provider 判断

`LocalActivationDirectory.CollectIdleAsync(...)` 过去直接读 `DateTimeOffset.UtcNow`。

现在它改成使用注入的 `TimeProvider` 取当前时间，再和 activation 的 `LastTouchedUtc` 比较。

这样一来：

- activation 触碰时间
- idle collect 当前时间

终于进入了同一套解释。

### 2.4 callback activation 也跟着走同一时间源

这轮没有把 callback target 当特例继续留在旧路径，而是把 `LocalCallbackDirectory` 创建的 activation 也一起接进同一个 `TimeProvider`。

这件事看起来小，但它很关键，因为它保证了：

`callback activation 没有成为 activation 时间收敛里的漏口。`

### 2.5 `ManualTimeProvider` 不再只是“会改 UtcNow”

测试侧也往前推了一步。

之前的 `ManualTimeProvider` 只能：

- 返回一个可控 `UtcNow`

现在它还支持：

- `CreateTimer(...)`
- 在 `Advance(...)` 时驱动到期 timer callback

这说明当前测试里的“可控时间”不再只覆盖 metadata / retention 判断，而是开始能覆盖 timer fire 本身。

## 3. 这一轮最关键的边界是什么

这轮最重要的不是“整个 runtime 的所有时钟都统一了”。

这话现在还不能说。

更准确的说法是：

`activation 本地时间边界已经开始统一到注入的 TimeProvider 上。`

这里说的 activation-local，当前主要包括：

- activation `LastTouchedUtc`
- activation-owned timer due/period
- idle collect 的当前时间
- callback activation 的本地时间语义
- warm handoff capture 时间

这层边界一旦先收干净，后面 reminder、activation recovery、更多 lifecycle 时间判断才有稳定落点。

## 4. 这一版最值得看的验证

这轮最值钱的验证不是“字段从哪里取时间”，而是：

### 4.1 timer fire 可以只靠推进 `ManualTimeProvider`

现在可以这样验证：

1. 注册一个 one-shot timer
2. 不推进 provider 时，snapshot 还是空
3. 推进到 due time 前一刻，snapshot 还是空
4. 再推进 1ms，timer callback 就会进入 activation

这说明当前 timer 已经不再依赖真实 wall clock 才能动起来。

### 4.2 idle collect 不再需要真实等待

现在可以这样验证：

1. 创建 fast grain 和 slow grain
2. 只推进 `ManualTimeProvider`
3. 调 `CollectIdleAsync(...)`
4. fast grain 被收掉，slow grain 保留

这说明 activation touch 时间和 collect 当前时间，已经进入同一套可控时间语义。

## 5. 为什么这步对后面继续往下推很重要

这一步真正带来的好处，不只是“测试更快”。

更关键的是，runtime 里跟 activation 生命周期直接相关的本地时间判断，终于开始有一个明确承载点：

- provider 从 builder 注入
- activation / callback / local directory 消费
- 测试可以用同一套时间源驱动

这个骨架一旦有了，后面继续往下长时，很多东西就不用重新切边界：

- reminder 本地调度入口
- activation 恢复后的时间判断
- lifecycle 相关回收窗口
- 更多 deterministic runtime tests

## 6. 当前故意还没说成“全运行时时间统一”

这里必须讲清楚。

这轮现在做到的是：

- activation 本地时间边界收敛
- timer / idle collect / callback activation / handoff capture 开始共享同一时间源
- 测试侧能够用受控时间驱动 timer 和 idle collect

这轮还没有做到的是：

1. membership 相关时钟全部迁到 `TimeProvider`
2. transport 故障注入 / 延迟注入的所有时间语义全部统一
3. reminder / 持久定时链
4. timer 恢复、timer checkpoint、timer failover
5. 整个 runtime 所有时间判断的全局收敛

所以这一轮最准确的定位不是：

- “时间系统已经做完了”

而是：

- “activation-local time 这条线终于不再分裂成两套时钟了”

## 7. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的说法是：

`OrleansReplicaKernel 已经把 activation 本地时间语义开始统一到注入的 TimeProvider 上：activation-owned timer、LastTouchedUtc、idle collect、callback activation 与 handoff capture 现在共享同一时间源，并且可以用受控时间做确定性验证；但这还不是整个 runtime 时间系统的最终形态。`
