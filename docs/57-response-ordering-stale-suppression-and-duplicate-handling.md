# 响应排序、过期抑制与重复处理（第五十七篇）

## 1. 这一篇补哪块

前两篇其实已经把两条最粗的线补上了：

- target 侧 dedupe：同一个 `RequestId` 不重复执行业务方法
- source 侧 completion：source 只等待自己当前还在等的 `AttemptId`

但这还不够。

因为在真正的分布式调用里，response 不是只会“到”或者“不到”，它还会出现三种更麻烦的情况：

1. 旧 attempt 的 response 在新 attempt 成功之后才到
2. 同一个 response 被网络层、传输层、测试注入或程序 bug 重复投递
3. caller 已经结束等待，但 source 还需要分清这到底是 late、stale，还是 duplicate

如果 source 侧没有再多记一层“这个 request 目前推进到了哪个 attempt、又是哪个 attempt 赢了”，那实现最后就只能把所有东西都含糊地记成：

`no pending completion, discard`

行为也许不一定错，但语义已经脏了。

## 2. 为什么 `AttemptId` 还不够

`AttemptId` 解决的是：

`source 现在具体在等哪一次发送`

但它解决不了：

`这个逻辑 request 的时间线已经推进到哪里了`

举个最典型的例子：

1. `RequestId = R`
2. attempt #1 发出去了
3. target 执行完成，但 response 在 source 侧投递失败
4. source 发 attempt #2，还是同一个 `RequestId = R`
5. target 不重新执行，只 replay cached response 给 attempt #2
6. attempt #2 成功返回给 caller
7. 这时 attempt #1 那个旧 response 又被“漏回来”了

这第 7 步里，source 看这个 response：

- `AttemptId` 已经过期了
- 但它不是普通 late response
- 它是一个被更新 attempt 淘汰掉的 stale response

这两个概念不能再混。

## 3. Source 侧真正需要记住什么

当前阶段最小但够用的模型其实就三件事：

1. `LatestAttemptSequence`
2. `CompletedAttemptSequence`
3. `WaitingStopped`

也就是说，source 不光要知道：

- 我现在有没有在等这个 `AttemptId`

还要知道：

- 这个 request 已经推进到第几个 attempt
- 有没有某个 attempt 已经赢了
- caller 是主动不等了，还是被更新的 attempt 覆盖掉了

有了这层状态，source 才能把 response 分成几类：

- `accepted`
- `late`
- `stale`
- `duplicate`

## 4. 这四类到底怎么区分

### 4.1 accepted

当前 attempt 还在等，而且这个 response 正好属于它。

这是正常完成。

### 4.2 late

caller 已经不等了，但 source 这边也没有更“新”的 attempt 把它盖掉。

最典型的就是：

- caller 自己 timeout / cancel
- remote 其实继续执行完
- response 晚一点才回来

这时它是 late，不是 stale。

### 4.3 stale

这个 response 属于旧 attempt，但 source 已经推进到了更新的 attempt，甚至更新的 attempt 已经完成了。

最典型的就是：

- attempt #1 的 response 丢了
- source 发 attempt #2
- attempt #2 已经成功
- attempt #1 的旧 response 这时才漏回来

这时 source 要明确地说：

`你不是“来晚了”，你是已经过期了`

### 4.4 duplicate

同一个 winning attempt 的 response 已经被 source 接收过一次，后来又来了完全重复的一份。

这通常说明：

- transport 层重复投递
- response replay 逻辑写重了
- 或测试里故意注入了 duplicate

duplicate 不该再去碰 caller state，也不该被误记成 stale。

## 5. 为什么“全都当 late”是坏设计

如果 source 把 stale 和 duplicate 都粗暴记成 late，会立刻丢掉两件很重要的信息：

1. retry 逻辑到底是不是在稳定工作
2. transport / callback path 是不是存在重复投递

对完整复刻 Orleans 来说，这很致命。

因为你后面迟早要回答这些问题：

- retry 命中的到底是重新执行，还是 replay cached response
- 旧 attempt 的 response 有没有在 source 侧被正确淘汰
- response channel 有没有重复投递

这些东西如果在 runtime 里没有明确语义，最后就只能靠猜日志。

## 6. 当前阶段最小可落的实现

这版 `OrleansReplicaKernel` 里，最值得先做的是：

1. `InvocationMessage` 和 `InvocationResponseMessage` 带上 `AttemptSequence`
2. source runtime 维护按 `RequestId` 跟踪的 request state
3. pending completion 继续按 `AttemptId` 跟踪
4. response 到达时，先看 pending，再结合 request state 分类
5. 分类结果至少分成 `late / stale / duplicate`
6. 把分类计数暴露出来，便于测试和后续观测

这个模型很小，但已经把 source 侧真正需要的协议语义补出来了。

## 7. 最值得验证的两条链

### 7.1 stale response after retry

最值得跑的一条是：

1. attempt #1 已经执行完成
2. 但第一次 response delivery 失败
3. source retry，发 attempt #2
4. target replay cached response，attempt #2 成功
5. 之后 attempt #1 的旧 response 又晚到
6. source 必须把它记成 `stale`

这条链验证的是：

`source 是否真的知道“新 attempt 已经赢了”`

### 7.2 duplicate response after completion

另一条是：

1. 某次调用已经成功返回
2. 同一个 response 又被重复投递一次
3. source 必须把它记成 `duplicate`

这条链验证的是：

`source 是否能区分“旧 attempt 过期了”和“当前 winning attempt 被重复投递了”`

## 8. 这一版还没做什么

这一版故意还没做这些更重的东西：

1. response channel 的真正 ordering contract
2. 跨节点 callback target 的 response window
3. response cache eviction
4. 更细的 metrics / tracing tags
5. hedged requests / parallel attempts
6. 真正的 transport ack 协议

也就是说，这一篇补的是 source 侧最关键的认知层：

`这个 response 到底是正常完成、来晚了、已经过期了，还是压根是重复件`

## 9. 为什么完整复刻 Orleans 迟早都得补这层

一旦系统里有：

- retry
- timeout
- response replay
- owner 迁移
- transport 重投

那 source 侧就不可能再靠“有没有 pending completion”活着。

它必须知道：

- request 时间线推进到了哪儿
- 哪个 attempt 已经赢了
- 后面到的 response 该不该认

否则 runtime 虽然能“看起来跑起来”，但语义永远是糊的。

而完整复刻 Orleans，最怕的就是这种“跑是能跑，但 runtime 自己都说不清自己在干什么”的状态。
