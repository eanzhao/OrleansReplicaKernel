# Membership Probing、Gossip 与 Stabilization：别把“发现问题”和“处理问题”写成一件事（第四十六篇）

这一篇承接前面的 [44-membership-health-and-relocation-policy.md](./44-membership-health-and-relocation-policy.md) 和 [45-membership-epochs-view-changes-and-failure-detection.md](./45-membership-epochs-view-changes-and-failure-detection.md)。

44 讲的是 `membership`、`node health`、`owner relocation policy` 怎么分。45 讲的是 `epoch`、`view change`、`failure detector` 怎么分。

如果再往下看，就会碰到更贴近运行时真实节奏的一层：

> periodic probe、gossip、stabilization 这三件事，看起来都和“节点是否活着”有关，但它们其实分别解决的是“怎么发现”、“怎么传播”、“怎么别太快下结论”。

这三件事如果写成一坨，membership 就会从“稳态控制面”变成“全天候噪音源”。一旦这样，后面的 routing、directory、relocation、cache invalidation 都会被拖着乱抖。

---

## 1. 先说结论

如果我要重建一版 Orleans，我会把这三层说死：

- `periodic probe` 负责主动采样节点状态，给 failure detector 提供输入。
- `gossip` 负责把成员视图、health 结论、view change 传播出去，但它本身不负责下最终判断。
- `stabilization window` 负责给系统一个“别立刻翻盘”的缓冲期，避免刚看到一次抖动就迁 owner、失效 cache、重算目录。

这三层可以连起来，但不能互相吞职责。

如果混了，系统会立刻变形：

- probe 会被写成 membership 变更
- gossip 会被写成强一致同步
- stabilization 会被写成“等一等再看”的玄学延迟

这不是细节问题，是重建版 runtime 能不能稳住的分水岭。

---

## 2. periodic probe 到底是干什么的

probe 的角色很简单：主动问一声，别光等别人来报错。

### 2.1 probe 是证据采样，不是最终判决

periodic probe 至少要能做这些事：

- 看节点还能不能连上
- 看心跳或轻量 ping 是否超时
- 看连续采样结果有没有恶化
- 给 failure detector 提供时间序列证据

它做的是“采样”，不是“宣判”。

### 2.2 probe 不应该直接改 membership view

这点很重要。

一次 probe 超时，只能说明这一次没探到，不说明节点已经从集群里消失了。

正确顺序应该是：

1. probe 产生失败信号
2. failure detector 收集信号
3. health 发生变化
4. 必要时再推动 view change

如果把 probe 直接写成 membership 更新，membership 就会抖得很厉害。

### 2.3 probe 也不该绑死在 transport 上

probe 可以走 transport，但不等于必须走完整业务请求链。

更合理的是把它做成一个独立的轻量信号：

- 业务请求失败，不等于 probe 失败
- probe 失败，也不等于业务一定失败

这样 runtime 才不会把“健康探测”和“真实调用”混成一条线。

---

## 3. failure detector 和 probe 的关系

这两层要分开看。

### 3.1 probe 是输入，detector 是解释器

probe 负责给出原始信号：

- 成功
- 失败
- 超时
- 延迟过高
- 连续失败

failure detector 负责把这些原始信号整理成更高一层的状态：

- `Healthy`
- `Suspect`
- `Unhealthy`

也就是说，probe 负责“看见什么”，detector 负责“这意味着什么”。

### 3.2 detector 不能只看单点

如果 detector 只看一次 probe 的结果，整个系统会非常脆。

它至少应该看：

- 连续失败次数
- 最近一段时间的失败密度
- 是否已经超过稳定窗口
- 是否和 membership 变化叠加出现

这就是为什么 detector 更像一个状态机，而不是一个布尔值。

### 3.3 detector 也不能替代 policy

detector 只是说“这个节点现在有问题”。

它不应该直接决定：

- owner 是否迁移
- directory 是否重写
- cache 是否全清
- node 是否从 membership 中删除

动作还是要交给上层 policy。

---

## 4. gossip 到底传播什么

gossip 最容易被写成“把所有东西都广播一遍”，这是最不该走的路。

### 4.1 gossip 传播的是事实，不是动作

在这条链里，gossip 更适合传播这些东西：

- membership view 的版本
- 某节点的 health 状态
- 哪些节点已经 suspect 或 unhealthy
- 哪些 view change 已经发生

它传播的是“发生了什么”，不是“系统下一步怎么做”。

### 4.2 gossip 不是强一致同步

这点必须说清楚。

gossip 的价值就在于它便宜、快、可扩散，但它天然不是强一致。

所以它适合：

- 扩散成员事实
- 提前让其他节点知道旧 cache 可能失效
- 缩短收敛时间

它不适合：

- 作为唯一真相来源
- 作为 owner 迁移的最终裁判
- 作为每次 probe 的同步确认

### 4.3 gossip 的好处是让收敛更快

如果一个节点被判定为 `Suspect`，而且这个结论只停留在本地，那系统会有一段时间处在“大家各说各话”的状态。

gossip 的作用就是把这个事实尽快传播出去，让：

- locator cache 尽早失效
- relocation policy 尽早拿到最新事实
- 别的节点别继续把坏节点当成可用目标

这会明显减少后面一连串 retry 和 fallback 的浪费。

---

## 5. stabilization window 是干什么的

这一层是最容易被低估的。

### 5.1 stabilization 不是“故意慢半拍”

它不是为了拖延，而是为了防止误判。

当系统刚看到一轮 failure signal 时，直接做激进动作通常太早了。

stabilization window 给的是一个缓冲：

- 让 probe 继续采样
- 让 detector 继续积累证据
- 让 gossip 把新事实传播出去
- 让短暂抖动自己过去

### 5.2 stabilization 要约束的是动作，不是观察

我们要保留快速观察，但延迟重动作。

也就是说：

- `Suspect` 可以很快出现
- `view change` 不一定马上发生
- `owner relocation` 更不该立刻跟上

这个顺序能显著减少误迁移。

### 5.3 stabilization 适合做“迁移前的缓冲阀”

尤其对 owner relocation 来说，stabilization 很有价值。

原因很简单：

- 先怀疑，不等于先搬家
- 先失联，不等于先改 owner
- 先抖一下，不等于先 shed activation

如果没有这个缓冲层，整个 runtime 会像被噪音牵着走。

---

## 6. 这三层怎么串

如果把它们串起来，比较合理的链路是：

1. `periodic probe` 采样节点可达性和延迟
2. `failure detector` 汇总 probe 结果，更新 node health
3. `gossip` 把 health / view change 传播给其他节点
4. `stabilization window` 让系统在一小段时间内不立刻做激进动作
5. 证据足够后，再由 policy 决定是否触发 relocation、cache invalidation 或 membership 视图更新

这个顺序很重要，因为它把“发现问题”和“处理问题”拆开了。

---

## 7. 该继承哪些成熟经验

如果是重建版 runtime，我会继承 Orleans 这套思路里比较成熟的部分。

### 7.1 继承“证据和结论分离”

这是最值得保留的经验。

probe 不是结论，detector 不是动作，membership 不是健康面板，directory 不是故障处理器。

把层次切开，系统会清楚很多。

### 7.2 继承“用 epoch 切视图”

45 已经说过，epoch 是成员事实的版本线。

这条线应该保留，因为它能让：

- 旧 cache 失效
- 旧结论可追踪
- 旧 view 不会被误当成当前 view

### 7.3 继承“快速传播事实，但动作保守”

gossip 应该快，动作应该稳。

这个组合很值钱：

- 让坏消息尽快扩散
- 让迁移和重算不要过快

这就是稳定系统常见的做法。

---

## 8. 应该避免哪些历史包袱

重建的时候，最好别把一些老问题一起搬过来。

### 8.1 避免把 probe 写成全局心跳轰炸

全局周期广播很容易做，但通常不值得。

更好的方式是：

- 分层采样
- 局部探测
- 只传播必要事实

### 8.2 避免把 gossip 写成伪强一致

gossip 的意义在于扩散，不在于模拟 consensus。

如果把它写得像强一致协议，复杂度会暴涨，但收益不一定成比例。

### 8.3 避免把 stabilization 写成“神秘等待”

很多系统最后会把稳定窗口写成一段看不懂的 sleep 或 backoff。

这不行。

stabilization 必须是明确的策略：

- 缓冲什么
- 缓冲多久
- 缓冲期间允许哪些动作
- 缓冲结束后允许哪些动作

### 8.4 避免把所有失败都折叠成 membership 变更

这是最容易把系统写脏的地方。

一次 probe 失败、一次 remote timeout、一次 transport error，都不该直接升级成成员变更。

不然 membership 会变成一个噪音桶。

---

## 9. 对重建版 runtime 的建议

如果按现在这条重建路线，我会把这一层做成下面这个样子：

- `PeriodicProbeService` 只负责触发探测
- `IFailureDetector` 只负责把信号整理成 health
- `IClusterMembership` 只负责保存 view 和 epoch
- `IGossipChannel` 只负责传播 view change 和 health facts
- `IStabilizationPolicy` 只负责控制何时允许迁移和重算

这样做的好处很直接：

- probe 可以换实现
- gossip 可以换通道
- stabilization 可以换策略
- membership 不会被调度层污染

如果未来要从单节点 toy 继续往多节点推，这一层会比“先做一个很聪明的大类”更稳。

---

## 10. 这篇话最想留下什么

一句话：

> probe 负责发现，detector 负责解释，gossip 负责传播，stabilization 负责别太快动手。

这四件事必须分开写。

只要它们分开，membership 就不会轻易写脏；只要 membership 不脏，directory、routing、relocation 才能稳住。
