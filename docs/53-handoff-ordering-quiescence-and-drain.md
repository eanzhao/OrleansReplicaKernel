# Handoff 之前的顺序边界：Quiescence、Drain 与 Turn 完整性（第五十三篇）

这一篇承接前面的 [52-handoff-failure-rollback-and-fallback.md](./52-handoff-failure-rollback-and-fallback.md)。

前一篇已经把一个关键原则收住了：

> warm handoff 可以失败，但主路径不能跟着塌。

但只要再往前走一步，就会碰到另一个同样关键的问题：

> handoff 到底应该在旧 activation 的哪个时刻发生？

如果这个问题不先答清楚，前面那些 placement、rebalancing、warm handoff state transfer 很快都会变得不可信。

因为你最终一定会遇到这种场景：

1. 旧 activation 上有一个 turn 正在跑
2. 你这时决定要 handoff
3. 新 owner 也开始接请求

这时候如果边界不清楚，就会立刻冒出这些问题：

- 正在跑的 turn 算谁的？
- handoff state 捕获的是 turn 前的状态，还是 turn 后的状态？
- 旧 activation 会不会一边继续执行，一边已经把 owner 切走？
- 新 activation 会不会拿到一份还没稳定下来的状态？

这就是为什么 handoff 真正往下写时，`quiescence` 和 `drain` 是绕不过去的。

## 1. 先把三个词拆开

### 1.1 ordering

这里说的 ordering，不是全系统消息总序，而是一个 grain activation 自己的 turn 顺序边界。

最基本的问题其实只有一句：

> handoff 不能把一个 activation 的串行 turn 边界打碎。

### 1.2 quiescence

quiescence 指的是：

- 这个 activation 不再接受新的 turn 进入

也就是说，它不是“已经空了”，而是“开始进入静默期”。

### 1.3 drain

drain 指的是：

- 已经在跑或已经排队的 turn，先跑完

所以两者不是一回事：

- `quiesce` 是关入口
- `drain` 是等存量跑空

很多实现把这两个词混着用，但在 runtime 里，最好还是拆开。

## 2. 如果不做 quiesce + drain，会出什么问题

### 2.1 capture 到半截状态

如果你一边 handoff，一边旧 activation 还在继续跑 turn，那你抓到的 handoff state 可能只是半截。

这会导致：

- 新 activation 拿到的是旧状态
- 旧 activation 又继续把状态往前推进了一步

最后就会出现两边都“看起来像对的”，但整体顺序其实已经断了。

### 2.2 旧 owner 和新 owner 同时处理请求

如果 owner 记录已经改了，但旧 activation 还在继续接 turn，就会出现非常脏的双执行窗口。

actor runtime 最怕的就是这种窗口。

因为它会直接破坏大家默认相信的那件事：

> 同一个 grain activation 的 turn 是串行的。

### 2.3 warm handoff state 和 turn 结果对不上

这也是最常见的坑。

比如：

1. 一个 turn 还没跑完
2. 你先 capture 了 handoff state
3. turn 结束后又改了内存

那新 activation 接到的 state，就天然已经落后了一步。

## 3. 如果要重建一版，我会把边界切成这样

我会坚持下面这条顺序：

1. 旧 activation 进入 quiescing
2. 拒绝新的 turn
3. 等现有 turn drain 完
4. 再 capture handoff state
5. 再 stage 到目标 node
6. 再切 directory owner
7. 再让新 activation 开始接后续调用
8. 最后再清旧 activation

最关键的是：

> capture 必须发生在 drain 之后，而不是之前。

不然 handoff state 的时间点就不可信。

## 4. 哪一层负责哪一段

### 4.1 scheduler / activation 负责 turn 边界

这层最清楚：

- 现在是不是还有 turn 在跑
- 能不能再接新的 turn

所以 quiesce + drain 最适合落在 activation 边界，而不是放进 directory。

### 4.2 handoff coordinator 负责“等它安静下来再搬”

也就是 host 或 handoff 协调层，只需要做一件很朴素的事：

> 别急着 capture，先等旧 activation 安静下来。

### 4.3 directory 只在正确时机记录新 owner

directory 不该自己去猜：

- turn 有没有跑完
- activation 能不能停

它只该在上游已经确认 quiesce + drain 之后，再写 owner 事实。

### 4.4 runtime retry 只兜 quiescing 期间的短暂拒绝

如果一个调用刚好撞上 quiescing activation，被拒绝也不奇怪。

更合理的做法是：

- 识别“这是 quiescing，不是业务失败”
- invalidate locator
- 稍等一下再重试

这样请求会更自然地落到新 owner。

## 5. 这一步和 fallback 的关系

这一步不是替代 fallback，而是把 fallback 的时机也理顺。

比如：

- apply 失败，可以退回 cold activation
- capture 失败，可以退回 cold handoff

但这些决定都应该发生在：

- activation 已经 quiesced
- 现有 turn 已经 drained

之后。

也就是说：

> fallback 解决的是“状态带不过去怎么办”，quiesce/drain 解决的是“旧 turn 什么时候算真的结束”。

这两个问题不能互相替代。

## 6. OrleansReplicaKernel 这一版怎么落

这次原型里，我把这一步也故意做得很窄。

### 6.1 ActivationEntry 增加 quiescing 状态

现在 activation 会有一个明确的 quiescing 状态：

- 一旦开始 handoff，旧 activation 先进入 quiescing
- 新 turn 会被拒绝

这不是失败，而是一种临时的迁移动作。

### 6.2 等 pending turn 归零，再 capture

当前实现会在旧 activation 上：

- 先等 `_pendingInvocationCount` 归零
- 再 capture warm handoff state

这保证 capture 的时间点发生在旧 turn 都结束之后。

### 6.3 runtime 识别 quiescing 并做最小重试

如果调用恰好撞上 quiescing activation，runtime 会把它当成可重试信号：

- invalidate locator
- 稍等一下
- 再试一次

这不是完整迁移协议，但已经足够把“短暂迁移窗口”从“业务失败”里分离出来。

### 6.4 Program 增加一条真正的 drain 演示

这次 demo 新增了一条 `echo/drain`：

1. 先发一个慢调用
2. 调用还没结束时，就开始 `SetOwnerAsync`
3. handoff 不会立刻 capture
4. 要等慢调用结束，旧 activation drain 完
5. 然后再切 owner
6. 下一次调用在新 owner 上继续接着跑

这条演示的重点不是并发压测，而是把边界先钉死：

> in-flight turn 先完整结束，handoff 再发生。

## 7. 这套切法为什么更像一个成熟 runtime

因为它没有追求“迁移必须瞬间完成”，而是先把最值钱的保证立住了：

- turn 不被截断
- handoff state 时间点可信
- owner 切换不会和旧 turn 乱序叠在一起

这其实比“状态搬得多漂亮”更重要。

Orleans 真正给你的经验，不是某个神奇算法，而是这种优先级：

> 先保 turn 语义，再谈迁移体验。

## 8. 这一步还故意没做什么

当前版本仍然没有做：

- 真正的双写/双读迁移窗口控制
- 更细的 queued turn drain 策略
- handoff token / epoch fencing
- 更完整的 quiescing 请求转发协议

也就是说，这一版只是把“handoff 前先 quiesce + drain”立住，而不是把完整迁移协议一次做完。

但这一步已经足够重要，因为它会让后面的任何优化都站在正确顺序边界上。

## 9. 一句话结论

> handoff 不是“决定要搬就立刻搬”，而是“先关旧入口、等旧 turn 跑完、再抓状态、再切 owner”；quiesce 解决入口，drain 解决存量，ordering 才因此可信。
