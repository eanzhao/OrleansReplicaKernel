# Grain Interleaving 元数据与 Allow-Interleaving Turn（第六十八篇）

这一篇接在 `67` 后面，继续看 generated grain implementation metadata 怎么开始真正影响 runtime 行为。

上一轮我们把 metadata 接到了 activation 生命周期和 initial placement，这一轮则把它继续往调度层推进：让某个 grain type 可以声明“哪些方法允许 interleave”，然后由目标端 activation 在真正执行时决定这些 turn 能不能并发重叠。

这里我刻意没有直接做“整个 grain 可重入”。原因很简单：那一步太大了，会一下子把 call-chain、deadlock avoidance、reentrancy depth、callback 递归这些复杂度全拉进来。对于 `OrleansReplicaKernel` 当前这个阶段，更稳的切法是先把边界收在：

- 默认还是单 activation 串行 turn
- 只有显式标记的方法才允许 interleave
- 这个决定来自目标端 grain metadata，不来自调用方消息

## 1. 这轮到底加了什么

这一轮补的是一份很窄的 scheduling manifest：

- `GeneratedGrainImplementationAttribute.InterleavableMethods`
- `GrainTypeSchedulingPolicy`
- `ActivationScheduler` 的 `exclusive / interleavable` 双模式调度

对应关系很简单：

1. 生成代码在 grain implementation metadata 上声明某些方法名
2. builder 把它们装成 `GrainTypeSchedulingPolicy`
3. `LocalActivationDirectory` 创建 activation 时，把这份 policy 交给 `ActivationEntry`
4. `ActivationEntry.InvokeAsync(...)` 在目标端根据 `message.Invokable.MethodName` 查 policy
5. `ActivationScheduler` 决定这个 turn 是排成独占，还是允许和前面的 interleavable turn 重叠执行

也就是说，调度权限现在掌握在目标端 runtime 手里，不是调用方随手往 message 里塞个 flag 就能改执行语义。

## 2. 为什么不是直接做 grain reentrancy

因为这两件事不是一回事。

`allow-interleaving turn` 解决的是：

- 某些明确可并发的方法，不必被 activation 的默认串行队列完全堵住

它还没有解决：

- 同一 call-chain 的递归进入
- grain 调 grain 再回调自身时的死锁规避
- callback / observer / reminder / timer 的重入策略
- reentrancy depth 和 loop prevention
- request ordering 与 interleaving 的组合语义

所以这一轮更准确的定位，不是“我们已经做了 Orleans 的 reentrant grain”，而是“我们先把 reentrancy 真正需要依赖的第一层调度分叉做出来了”。

## 3. 当前调度语义

现在 `ActivationScheduler` 里有两类 turn：

- `exclusive`
- `interleavable`

规则是：

- 默认方法都是 `exclusive`
- 被 metadata 标记的方法才是 `interleavable`
- 多个连续的 `interleavable` turn 可以并发启动
- 只要队列头部碰到 `exclusive` turn，它就会变成一个屏障
- `exclusive` turn 必须等前面的 interleavable turn 全部结束后才能开始
- 当 `exclusive` turn 正在执行时，后面的 interleavable turn 也不能绕过去

这个语义很重要，因为它说明当前实现不是“谁能并发谁就直接飞”，而是保留了队列顺序里的 barrier 语义。

## 4. 在 demo 里怎么落的

这轮 demo 里，`EchoGrain` 的 generated metadata 现在会声明：

- `PingSlowAsync` 是 interleavable

所以同一个 `echo/interleaving` activation 上连续发两个 `PingSlowAsync` 时，它们会重叠执行。

但像 `PingAsync`、`PingWithObserverAsync` 这类没有被标记的方法，仍然是独占 turn。

也就是说，当前效果不是“EchoGrain 整个 grain 都可重入”，而是：

- `PingSlowAsync` 可以 interleave
- `PingAsync` 仍然要守默认独占语义

## 5. 这轮验证了什么

我这轮加了两层验证。

第一层是 scheduler 自己的单元测试：

- interleavable turn 确实能重叠
- exclusive turn 会等前面的 interleavable turn drain 完
- 后来的 interleavable turn 也不能绕过排在前面的 exclusive turn

第二层是 host/runtime 集成测试：

- `EchoGrain.PingSlowAsync` 两次连续调用的总耗时明显小于串行两次相加
- 一个 interleavable `PingSlowAsync` 在跑时，后面的 `PingAsync` 仍然会被挡住

这两层合在一起，说明这一轮不是只把 metadata 存起来了，而是真的穿透到了 runtime 行为。

## 6. 这版实现故意保留的限制

这一轮我故意没有把事情做得太“像已经完成版”。

当前限制包括：

- `InterleavableMethods` 目前按方法名匹配，还没有处理重载签名
- 还没有 call-chain reentrancy
- 还没有 `[AlwaysInterleave]` 这类更细粒度入口
- 还没有 timer / reminder / observer callback 的调度分类
- 还没有和 request ordering、cancellation、deactivation 做更复杂的组合验证

这些都不是漏做，而是刻意后放。因为现在更重要的是先把 scheduling metadata 这条线接上，确保未来扩展 reentrancy 时，不需要再反过来重切 builder、activation directory、activation entry、scheduler 这些边界。

## 7. 对完整复刻 Orleans 的意义

这一轮的价值，不在于“功能上多了一个并发开关”，而在于 `OrleansReplicaKernel` 现在第一次把：

- grain implementation metadata
- runtime activation manifest
- scheduler turn policy

这三层真正串起来了。

这件事一旦串起来，后面很多 Orleans 里真正难的能力才有地方挂：

- grain 级 reentrancy
- method 级 interleaving 策略
- callback / observer / timer 的特殊调度语义
- 更正式的 grain property manifest

所以这篇对应的结论是：

`68` 不是把 Orleans 的并发模型完全做完了，而是把“调度语义必须由目标端 metadata 决定”这条边界，第一次落成了可运行代码。后面真要继续追 Orleans 的 reentrancy，这一层就不用重来。
