# Membership TimeProvider 收敛与可控 View Change 时间戳（第七十三篇）

这一篇接在 `72` 后面，继续把 runtime 里的时间边界往前收。

`72` 解决的是 activation-local time 这条线：

- activation-owned timer
- `LastTouchedUtc`
- idle collect
- callback activation
- warm handoff capture

这些东西已经开始共享同一套注入的 `TimeProvider`。

但当时 membership 这条线还留着一个明显的漏口：

- `InProcessClusterMembership.Register(...)`
- `InProcessClusterMembership.SetHealth(...)`

这两处在生成 `MembershipViewChange.CreatedAtUtc` 时，还是直接读系统时钟。

所以 runtime 这时候会出现一个很尴尬的状态：

- activation 相关时间已经可以受控
- membership view change 时间戳却还在另一套时钟里

这一篇补的，就是把 membership authoritative history 的时间戳，也开始收进同一条 injected time 轨道。

## 1. 为什么 membership 的时间戳不能继续裸吃 wall clock

这里的问题不是“日志时间看着有点乱”这么轻。

### 1.1 membership history 本身就是协议事实的一部分

`MembershipViewChange` 里有：

- `Epoch`
- `NodeName`
- `PreviousStatus`
- `CurrentStatus`
- `Reason`
- `CreatedAtUtc`

前面那些字段当然更核心，但 `CreatedAtUtc` 也不是可有可无的装饰。

它至少会影响这些事：

- 本地 view 什么时候观察到某次变化
- gossip / stabilization 相关调试时如何解释顺序
- checkpoint 恢复后，历史事实看起来发生在什么时候

所以它虽然不是 authoritative truth 的唯一依据，但它也不是一条随便写写的日志时间。

### 1.2 可控时间测试会在 membership 这层断掉

如果 activation 侧已经能用 `ManualTimeProvider` 驱动，而 membership 还不行，就会出现很奇怪的断层：

- activation timer 可以靠推进 provider 触发
- idle collect 可以靠推进 provider 验证
- membership view change 时间戳却仍然要依赖真实时间

这会让 runtime 的 deterministic test story 只完成一半。

### 1.3 restore 之后的 membership 会重新掉回系统时钟

即便初次构建时给 membership 接了 provider，如果 restore 路径没接进去，问题还是在：

- checkpoint 里的旧 view change 时间戳是历史事实
- 但 restore 之后新产生的 view change 又开始吃 wall clock

这样同一条 membership history 就会重新变成半边 provider、半边系统时钟。

所以这轮真正要补的不是“构造函数多传一个参数”，而是：

`membership authoritative history 在创建和 restore 后继续演进时，都要站在同一时间源上。`

## 2. 当前这版具体做了什么

这轮改动很小，但边界很干净。

### 2.1 `InProcessClusterMembership` 现在持有 `TimeProvider`

membership 自己开始保留注入时间源，默认仍然回退到 `TimeProvider.System`。

这意味着：

- `Register(...)`
- `SetHealth(...)`

在生成新的 `MembershipViewChange` 时，不再直接读 `DateTimeOffset.UtcNow`。

### 2.2 builder 构建和 restore 路径都把 provider 传下去了

这点非常关键。

如果只有新建路径传 provider，而 restore 还没传，那只能算“半修”。

当前这版 builder 已经把 `_timeProvider` 同时传给：

- 新建的 `InProcessClusterMembership`
- `InProcessClusterMembership.Restore(...)`

所以 membership 这条线在重启恢复后继续演进时，不会重新掉回系统时钟。

### 2.3 新测试不再只断言 epoch，也开始断言时间戳

这一轮测试重点不是“membership 能不能工作”，那早就已经能工作了。

更关键的是两条新的时间语义验证：

1. 在同一个 `ManualTimeProvider` 上推进时间
   - `Register`
   - `SetHealth -> Suspect`
   - `SetHealth -> Unhealthy`
   - 三条 view change 的 `CreatedAtUtc` 应该精确对应推进后的时间
2. export checkpoint 后 restore 到另一个 `ManualTimeProvider`
   - restore 之后再做新的 `SetHealth`
   - 新的 view change 时间戳应该继续来自 restore 时注入的 provider

这两条一起成立，才说明：

- membership 新建路径接通了
- restore 后继续演进的路径也接通了

## 3. 这轮最关键的设计点

这轮要守住的边界其实很简单：

`checkpoint 里的历史时间戳是历史事实；restore 之后新增的历史时间戳，必须继续从当前注入时间源生成。`

也就是说：

- 旧历史不该被 rewrite
- 新历史也不该偷偷掉回系统时钟

这个判断虽然保守，但很重要，因为它保证了 membership history 在 restore 前后都能保持可解释。

## 4. 这步为什么对后面继续推进很关键

如果把 `58`、`72`、`73` 串起来看，会发现 runtime 的时间语义开始出现一条更清楚的主线：

1. response history retention 先走 `TimeProvider`
2. activation-local time 跟上
3. membership authoritative history 也开始跟上

这条线的意义，不在于“字段统一得更漂亮”。

真正重要的是：

`runtime 里那些会被 checkpoint、恢复、传播、调试一起消费的时间事实，开始有统一承载点了。`

这样后面再往下推：

- stabilization 时间窗口
- gossip tick 验证
- membership 恢复后的时间行为
- 更完整的 deterministic cluster tests

就不用每一层都重新解释“这块时间到底归谁管”。

## 5. 当前故意还没说成“membership 时间系统完成”

这里同样要讲清楚。

这轮当前做到的是：

- `MembershipViewChange.CreatedAtUtc` 改成吃 injected `TimeProvider`
- membership create / restore 两条路径都能继续沿用同一时间源
- 测试可以用可控时间精确断言新的 view change 时间戳

这轮还没有做到的是：

1. membership 相关所有时间窗口都统一完
2. stabilization / gossip 的所有时间行为都可控完
3. transport 故障注入与 membership 观测时间完全并轨
4. 更大范围的 cluster deterministic time story

所以这轮更准确的定位不是：

- “membership 时间问题做完了”

而是：

- “membership authoritative history 的时间戳不再游离在统一时间边界之外了”

## 6. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的说法是：

`OrleansReplicaKernel 已经把 membership authoritative history 的时间戳开始统一到注入的 TimeProvider 上：Register / SetHealth 生成的 MembershipViewChange 现在和 builder / restore 路径共享同一时间源，并且可以用受控时间做确定性验证；但这还不是 membership 全部时间语义的最终形态。`
