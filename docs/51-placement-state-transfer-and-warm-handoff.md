# 51. placement 之后的状态转移边界：warm handoff 该怎么切

这一篇承接前面的 [50-placement-policy-rebalancing-and-handoff.md](./50-placement-policy-rebalancing-and-handoff.md)。

前一篇已经把三层关系拆开了：

- `initial placement` 负责第一次落点
- `rebalancing` 负责判断要不要迁
- `handoff` 负责真的切 owner

但 handoff 真正往下写时，马上会碰到一个更麻烦的问题：

> owner 切过去以后，新 activation 到底要不要接旧 activation 的状态？

这个问题如果不单独切出来，架构很快又会脏掉。

因为你一旦写得含糊，后面很容易把下面这些东西混成一坨：

- placement decision
- directory owner record
- activation lifecycle
- in-memory state transfer
- persistent state reload
- checkpoint recovery

这几层名字都像“恢复”或“迁移”，但其实完全不是一回事。

## 1. 先把几个相近概念分开

### 1.1 cold handoff

最简单的 handoff，是只做 owner 切换：

1. directory 把 owner 从 A 改到 B
2. locator 失效
3. A 上旧 activation 退出
4. 下次请求打到 B
5. B 新建一个 activation，从空内存开始处理

这个模型的好处是简单、干净、稳定。

坏处也很直接：

- 旧 activation 里的瞬时内存全没了
- 如果业务状态不在持久层，行为会跳变
- “刚迁过去第一次请求”常常会表现成冷启动

### 1.2 warm handoff

warm handoff 不是“把对象搬过去”，而是：

1. 旧 activation 导出一份很窄的、可转移的状态
2. handoff 协调层把这份状态交给新 owner
3. 新 activation 创建后决定要不要接这份状态

这里最关键的一点是：

> 被转移的是“状态快照”，不是“对象本体”。

也就是说，新 activation 还是新实例，只不过它在启动时拿到了一份额外输入。

### 1.3 persistent state reload

这又是另一件事。

如果 grain 的真实状态本来就在持久层，那 owner 迁过去以后，新 activation 完全可以重新读存储。

这时候即便没有 warm handoff，语义也未必错。

所以 warm handoff 不是 persistent state 的替代品。
它更多是：

- 减少冷启动跳变
- 传递一小段短期上下文
- 避免“刚迁过去时完全失忆”

### 1.4 checkpoint recovery

checkpoint 也不是 warm handoff。

checkpoint 解决的是：

- 进程重启后，runtime 元数据怎么更快恢复

warm handoff 解决的是：

- owner 已经切了，但希望新 activation 接到一小段来自旧 activation 的运行时状态

这两件事发生在不同时间点，依赖也完全不同。

## 2. 为什么这一层特别容易写脏

因为从业务感觉上看，大家都像是在问：

> “迁过去以后，状态还在不在？”

于是很容易一路滑到下面这些错误做法：

### 2.1 直接复制整个 grain 对象

这是最应该避免的写法。

原因很简单：

- grain 里可能握着 scheduler、timer、回调、资源句柄
- 里面可能混着 runtime 注入的对象
- 对象图里可能还有根本不该跨节点流动的引用

一旦你开始 copy 整个对象，handoff 层就会直接吃掉 activation lifecycle 的职责。

### 2.2 让 directory 顺手保存业务状态

directory 应该只记 owner 事实。

如果 directory 开始顺手保存一份“最新业务状态”，它马上就不再只是路由事实层了，而会变成半个缓存、半个状态仓库。

这会把后面的一致性问题全部拖进来。

### 2.3 让 placement policy 决定怎么搬状态

placement policy 最好只给出“放哪儿更合适”。

状态怎么导出、什么时候导出、失败了怎么回退，这些都不该放进去。

不然 placement policy 很快就会从“决策器”长成一个小型 runtime。

### 2.4 把 warm handoff 和 checkpoint 复用成同一份数据

这也很诱人，但通常不对。

因为：

- checkpoint 面向 restart
- warm handoff 面向在线迁移

它们的保真度、时效性、失败语义都不同。

一个是“重启后别从零开始”，一个是“迁 owner 时别全丢”。

## 3. 如果要重建一版，我会怎么切

我会把这一层收成下面四个边界。

### 3.1 grain 自己决定“有没有可转移状态”

也就是 grain 可以 opt-in。

有些 grain 适合 warm handoff：

- 会话型短期状态
- 少量聚合缓存
- 可序列化的本地计数

有些 grain 根本不适合：

- 状态本来就在持久层
- 内部握着大量运行时对象
- in-memory 状态太大，不值得搬

所以最干净的方式不是“所有 grain 一律支持迁移”，而是：

> grain 明确声明自己能不能导出一份 handoff state。

### 3.2 handoff 协调层只处理一个窄信封

handoff 协调层不需要知道业务字段。

它只需要处理一份抽象信封：

- 这是哪个 grain 的 state
- 什么时候捕获的
- 里面带了一份 payload

至于 payload 里面是：

- 计数
- 会话 token
- 某段聚合后的上下文

那是 grain 自己的事。

### 3.3 新 activation 在创建时决定要不要接

这一点非常重要。

warm handoff state 最好不要直接灌进 directory 或 runtime。

而应该是：

1. 先把 state 暂存在目标 node 的 activation directory
2. 等真正命中这个 grain 时，再创建新 activation
3. 新 activation 创建后，显式 apply 这份 state

这样做的好处是：

- handoff 不会强迫目标 node 立刻 eager create activation
- activation lifecycle 还是 activation directory 自己负责
- runtime 不需要知道业务字段

### 3.4 如果拿不到状态，就退回 cold handoff

这也是必须收死的一条。

warm handoff 只能是增强项，不能变成强依赖。

比如：

- 源 activation 已经没了
- 源 grain 没有实现 handoff participant
- handoff payload 无法使用

这时系统应该明确退回：

- 切 owner
- 新建 activation
- 从冷状态继续

而不是把整条 handoff 卡死。

## 4. Orleans 给我们的启发

Orleans 本身没有把“在线 owner 切换 + 运行时状态转移”做成一个特别强的显式主能力。

这反而说明一件事：

> 真正稳定的 actor runtime，更依赖清晰的 activation 生命周期和持久状态语义，而不是激进的对象迁移。

所以如果你要重建一版，不应该上来就追求“任意 grain 热迁移”。

更靠谱的路线是：

1. 先把 cold handoff 做对
2. 再给少数适合的 grain 增加 opt-in 的 warm handoff
3. 最后再决定要不要把它做成可插拔能力

这条路线更稳，也更接近 Orleans 已经替你踩过坑之后留下的经验。

## 5. OrleansReplicaKernel 这一版怎么落

这次 toy 里，我故意没有做“对象搬家”。

做法是：

### 5.1 新增一个 grain 侧 opt-in 接口

名字是：

- `IActivationHandoffParticipant`

它只做两件事：

- `CaptureHandoffState()`
- `ApplyHandoffState(object? state)`

也就是说，toy 先把这层边界立起来，而不是先追求 payload 的漂亮类型系统。

### 5.2 handoff 记录是单独的 runtime 信封

toy 新加了一份：

- `ActivationHandoffRecord`

它只保存：

- grain id
- instance type name
- payload
- capture 时间

注意这里仍然没有把 directory 变成状态层。

directory 只是在目标 node 本地暂存一份“待应用的 handoff state”。

### 5.3 只有 EchoGrain 先 opt-in

这次 demo 里：

- `EchoGrain` 支持 warm handoff
- `CounterGrain` 仍然不支持

这是故意的。

因为这样你就能同时看到两种语义：

- `EchoGrain` 在 owner 切换时可以延续短期内存计数
- `CounterGrain` 仍然依赖实例重新创建，所以 checkpoint recovery 之后不会延续旧 total

### 5.4 activation metadata recovery 仍然是另一层

这次新增的 warm handoff，没有去复用 activation metadata checkpoint。

原因很简单：

- activation metadata recovery 是 restart 加速器
- warm handoff 是在线迁移时的短期状态桥

它们不该共用一个抽象。

## 6. 这套切法为什么更干净

因为职责仍然清楚：

- placement policy 只选位置
- rebalancing policy 只决定值不值得迁
- handoff 协调层只转交一个窄信封
- activation directory 只在目标 node 暂存和消费这份信封
- grain 自己决定 state 怎么导出、怎么恢复

这意味着以后你要继续往前长，也还有空间：

- 可以把 payload 从 `object` 收成 codec/serializer
- 可以加 handoff 超时和失败回退
- 可以让 warm handoff 只对少数 grain type 开启
- 可以把 handoff state transport 抽成独立消息层

但无论怎么长，都不需要把 directory、placement、checkpoint 再揉回一团。

## 7. 这一步对“重建版 Orleans”的实际意义

如果你的目标真的是重写一版，而不是忠实复刻全部历史形状，那这一步很重要。

因为它会逼你提前做一个选择：

> 你到底是想做“会迁对象的神奇 runtime”，还是想做“边界清楚、能力逐层加上去的 runtime”？

我的建议还是后者。

也就是：

- 默认只保证 cold handoff 正确
- 把 warm handoff 当成一个 opt-in 能力
- 明确它只转移窄状态，不转移 activation 本体

这条线会让你后面接 persistent state、stream、timer、transaction 时都更轻松。

## 8. 一句话结论

> placement 决定去哪儿，handoff 决定什么时候切，warm handoff 只负责给新 activation 一份可选的小状态；它不是对象迁移，不是 checkpoint，也不是 persistent state reload。
