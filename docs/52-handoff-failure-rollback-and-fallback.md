# Handoff 失败时的退回与兜底：rollback 和 fallback 的边界（第五十二篇）

这一篇承接前面的 [51-placement-state-transfer-and-warm-handoff.md](./51-placement-state-transfer-and-warm-handoff.md)。

前一篇已经把 warm handoff 的基本边界立住了：

- 迁的是一份窄状态，不是 activation 对象本体
- grain 自己决定要不要导出这份状态
- 目标 activation 在创建时才决定要不要接

但只要你真往下写，马上就会碰到一个现实问题：

> 如果 warm handoff 失败了，系统该怎么办？

这个问题看着像异常处理，实际上是架构问题。

因为它会逼你回答：

- owner 还切不切？
- 旧 activation 要不要继续留着？
- 新 activation 是不是还能冷启动？
- directory 要不要回滚？
- checkpoint 要不要插手？

这些问题如果没切开，handoff 这条线很快又会长回“全能 runtime 黑盒”。

## 1. 先把“失败”分成几类

warm handoff 不是只有一种失败。

最少也要分成下面几类：

### 1.1 capture 失败

旧 activation 还在，但导出状态时失败了。

比如：

- grain 自己拒绝导出
- 导出逻辑抛异常
- 需要的数据已经不完整

这时候最重要的判断是：

> 失败的是 warm state capture，不是 owner move 本身。

也就是说，系统通常仍然可以继续 handoff，只是这次退回 cold handoff。

### 1.2 stage 失败

旧 activation 成功导出了状态，但目标 node 暂存这份 state 失败了。

比如：

- 目标 activation directory 不接受这份状态
- 目标节点临时不可用
- handoff 信封写入失败

这时候也要尽量退回：

- owner 继续切
- 新 activation 冷启动

而不是把整个迁移链卡死。

### 1.3 apply 失败

目标 activation 已经创建了，但在应用 handoff state 时失败了。

比如：

- payload 类型不对
- grain 自己校验不过
- 新版本 grain 不认旧 payload

这时候最干净的处理通常是：

- 丢掉这份 handoff state
- 保留新 activation
- 让它从冷状态继续

也就是说，不要为了“状态没接上”再把 activation 回滚掉。

### 1.4 目标节点不可用

这已经不只是 handoff state 的问题了，而是 routing / membership / placement 的问题。

如果目标节点已经不可用，那通常应该：

- 重新选 owner
- 或者放弃这次迁移

而不是指望 warm handoff 自己把问题兜住。

### 1.5 源 activation 已经没了

这也很常见。

比如：

- 刚准备 capture，源 activation 已经被 idle collect 了
- 节点故障时 activation 已经被 shed 掉了

这时候根本不该把它当错误。

更准确的说法是：

> warm handoff 条件已经不成立，所以自动退回 cold handoff。

## 2. rollback 和 fallback 不是一回事

很多实现会把这两个词混着用，但它们应该分开。

### 2.1 fallback

fallback 的意思是：

- 原本想走 warm handoff
- 现在条件不满足了
- 那就退回 cold handoff

它的目标是：

- 保证调用还能继续
- 不让可选增强项绑架主路径

### 2.2 rollback

rollback 的意思是：

- 已经做了一部分迁移动作
- 现在要不要撤回这些动作

这个动作代价更高，也更危险。

因为一旦回滚，你就要重新处理：

- directory owner record
- locator cache
- 旧 activation 是否已经停掉
- 新 activation 是否已经起来

所以对重建版 runtime 来说，一个很重要的原则是：

> 能 fallback 就尽量 fallback，别轻易做 rollback。

换句话说：

- warm handoff state 失败了，不等于 owner move 失败了
- state 没接上，不代表要把整个 handoff 回滚回去

## 3. 如果要重建一版，我会怎么定规则

我会把规则收成下面几条。

### 3.1 warm handoff 永远是 best effort

这条必须写死。

也就是：

- 它是增强项
- 它不是主语义
- 它不该阻塞 owner 切换

否则后面每加一个新能力，都会把 handoff 变成关键故障点。

### 3.2 cold handoff 必须始终可用

不管 warm path 怎么失败，系统都要能落回：

1. 改 owner
2. 失效 locator
3. 清旧 activation
4. 新 activation 冷启动

这条是整个迁移系统的保底语义。

### 3.3 directory 不做 rollback orchestrator

directory 只记事实：

- 当前 owner 是谁
- 版本是多少

它不该再负责：

- 暂存一堆回滚日志
- 判断 handoff state 有没有成功
- 替 activation lifecycle 做补偿

不然 directory 很快就会从事实层长成半个事务协调器。

### 3.4 activation directory 只兜住本地 apply 失败

这层最适合做的事是：

- 本地暂存 handoff state
- activation 创建时尝试 apply
- 如果 apply 失败，就记日志并丢弃 payload

这是一种很“本地化”的失败处理。

它不会把错误向外扩散成全局回滚。

### 3.5 host 或 handoff coordinator 只兜住 capture / stage 失败

如果 capture 或 stage 失败，最适合兜底的是上层 handoff 协调逻辑。

它只需要说一句很朴素的话：

> 这次 warm state 没带过去，但 owner 还是照切，后面走 cold path。

这就够了。

### 3.6 checkpoint 不参与在线补偿

checkpoint 是 restart recovery 的工具，不该被临时拉来参与在线 handoff 失败补偿。

不然你会得到一个很奇怪的系统：

- 在线迁移失败了
- 然后开始读 checkpoint 补状态

这在语义上就已经混层了。

## 4. 最容易犯的几个错

### 4.1 apply 失败后，把 owner 再切回去

这通常会把简单问题变复杂。

因为这时你已经做了：

- directory move
- cache invalidation
- 旧 activation shutdown

如果再切回去，很容易出现两边都不干净。

### 4.2 为了回滚，保留旧 activation 很久不退

这会把 activation lifecycle 搞脏。

你本来只是想让 warm handoff 更顺，结果变成：

- 旧实例要挂起多久？
- 新实例起来以后旧实例还能不能接流量？
- 双 activation 窗口怎么管？

这些问题一下都会冒出来。

### 4.3 把 warm handoff payload 写进 checkpoint

这看起来像复用，其实很危险。

因为 handoff payload 的生命周期往往很短：

- 只为这一次迁移服务
- 过时就没价值了

把它写进 checkpoint，既脏抽象，也容易把旧脏数据带到重启恢复里。

## 5. OrleansReplicaKernel 这一版怎么落

这次原型的实现，我故意把 fallback 做得很保守。

### 5.1 Host 兜 capture / stage

`OrleansReplicaKernelHost` 现在会：

- 尝试从源 activation capture warm handoff state
- 尝试把这份 state stage 到目标 node

但这两步都只是 best effort。

如果失败，日志会明确写：

- `fallback to cold handoff`

然后 owner 仍然继续切。

### 5.2 LocalActivationDirectory 兜 apply

`LocalActivationDirectory` 在目标 activation 创建时会尝试 apply staged state。

如果 apply 失败，它不会把 activation 整个撤销掉，而是：

- 记录一条 `fallback to cold activation`
- 丢掉这份 handoff state
- 让新 activation 继续从冷状态跑

这正是我前面说的“能 fallback 就别 rollback”。

### 5.3 EchoGrain 提供故障注入

为了让这条链真的能演示出来，当前实现给 `EchoGrain` 加了两个一次性故障注入点：

- 下一次 capture 失败
- 下一次 apply 失败

这样 demo 就能明确跑出两条路径：

- `apply` 失败，退回 cold activation
- `capture` 失败，退回 cold handoff

### 5.4 CounterGrain 继续保持不参与

`CounterGrain` 还是不参加 warm handoff。

这很重要，因为它提醒我们：

> 不是所有 grain 都该被强迫参加迁移状态转移。

有些 grain 的正确模型，本来就该是：

- checkpoint 恢复 runtime 元数据
- 新 activation 冷启动
- 业务状态自己从别的地方恢复

## 6. 这套切法为什么对“重建版 Orleans”更有价值

因为它会逼你坚持一个更成熟的原则：

> 把在线迁移里的“增强体验”从“主语义保底”里剥出来。

在这套原则下：

- 主语义是 cold handoff 一定成立
- warm handoff 只是尽力而为
- fallback 是默认兜底
- rollback 是尽量避免的昂贵动作

这比一上来就追求“热迁移一定成功”更稳。

也更接近一个经历过很多版本迭代之后，真正会沉淀出来的系统设计。

## 7. 一句话结论

> warm handoff 可以失败，但 owner move 不必跟着失败；capture、stage、apply 哪一段掉了，就在哪一层吞掉并退回 cold path，别把整个 runtime 拉进回滚泥潭。
