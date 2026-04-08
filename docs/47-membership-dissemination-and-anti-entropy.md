# Membership Dissemination、Partial Fanout 与 Anti-Entropy：怎么把成员事实传开，但不把系统传乱（第四十七篇）

这一篇承接前面的 [44-membership-health-and-relocation-policy.md](./44-membership-health-and-relocation-policy.md)、[45-membership-epochs-view-changes-and-failure-detection.md](./45-membership-epochs-view-changes-and-failure-detection.md) 和 [46-membership-probing-gossip-and-stabilization.md](./46-membership-probing-gossip-and-stabilization.md)。

44 讲的是 `membership`、`node health`、`owner relocation policy` 怎么分。45 讲的是 `epoch`、`view change`、`failure detector` 怎么分。46 讲的是 `probe`、`gossip`、`stabilization` 怎么分。

如果再往下走，就会遇到一个更现实的问题：

> 成员事实已经被发现了，也被判定了，但到底要怎么把它传到其他节点，同时还不把系统搞成一锅粥？

这就是这一篇要讲的。

在重建版 runtime 里，我会把它拆成三类传播手段：

- `full broadcast`
- `partial fanout`
- `anti-entropy`

它们看起来都和“传播成员事实”有关，但解决的问题根本不一样。

如果这三件事写成一坨，gossip 最后就会被逼着同时当传播层、缓存刷新层、真相层和一致性层。那样系统一定会脏。

---

## 1. 先说结论

如果我要重建一版 Orleans，我会把这三层说死：

- `full broadcast` 负责在成员视图变化很明确、影响面很大时，把事实一次性推开。
- `partial fanout` 负责大多数日常传播，用更小的成本让事实尽快扩散。
- `anti-entropy` 负责补洞，专门修复“有些节点没收到”或“有些节点看到的是旧版本”这种慢性不一致。

这三层可以配合，但不能互相代替。

如果混了，系统会立刻变形：

- full broadcast 会被当成默认传播方式，成本爆炸
- partial fanout 会被误当成最终真相来源
- anti-entropy 会被写成事后补锅的黑魔法

这不是实现细节，是 membership dissemination 的基本分层。

---

## 2. full broadcast 到底解决什么问题

full broadcast 的特点就一个字：全。

### 2.1 它适合“必须让所有人尽快知道”的场景

full broadcast 适合这些情况：

- 重大 view change
- 节点明确退出或被踢出
- cluster 里某个关键事实发生了版本跳变
- 需要尽快让所有 locator cache 失效

也就是说，full broadcast 解决的是“这件事太重要了，别慢慢传”。

### 2.2 它不是默认通信模式

full broadcast 很贵。

如果每次 `Suspect`、每次 probe 超时、每次临时抖动都全量广播，系统很快就会变成：

- 网络噪音大
- 消息量高
- cache 一直失效
- relocation 过于敏感

所以 full broadcast 应该只在“事实足够硬”的时候用。

### 2.3 它更像控制面上的强提醒

full broadcast 的作用不是“保证强一致”，而是“让大家尽快知道这件事已经很重要了”。

它是控制面动作，不是数据面回路。

---

## 3. partial fanout 到底解决什么问题

partial fanout 是更常用的默认选择。

### 3.1 它解决的是成本和覆盖率的平衡

partial fanout 的目标不是让所有节点立刻收到，而是：

- 用较低的传播成本
- 把成员事实尽快扩散到足够多的节点
- 让系统在短时间内大体收敛

这比每次都 full broadcast 要现实得多。

### 3.2 它适合日常 membership dissemination

在大多数情况下，节点 health、view change、epoch 更新都不需要一把打满全网。

更合理的是：

- 选一小部分节点传播
- 让传播路径形成扩散
- 让节点逐步趋同

这就是 partial fanout 的价值。

### 3.3 它不能当成真相源

partial fanout 只是传播策略，不是事实裁判。

它的结果是“尽快扩散”，不是“传播后就一定正确”。

如果把它写成真相层，就会出现两个问题：

- 某些节点没收到时，系统会误以为事实已经全局成立
- 某些节点收到旧信息时，旧事实会被当成当前事实

所以 partial fanout 一定要配 anti-entropy。

---

## 4. anti-entropy 到底解决什么问题

anti-entropy 是整个传播层里最容易被轻视的一块，但它其实很关键。

### 4.1 它解决的是“慢性不同步”

即使你有 full broadcast 和 partial fanout，也还是会出现这些情况：

- 某个节点短暂离线
- 某条传播路径丢了一跳
- 某个节点收到的是旧 epoch
- 某些缓存已经更新，某些还没更新

anti-entropy 的作用就是补这些洞。

### 4.2 它不是主传播路径

anti-entropy 不该承担“每次变更都靠它传开”的职责。

它更像后台校准：

- 对齐成员视图
- 比较版本
- 找出缺失的 view change
- 把落后的节点慢慢拉回一致

### 4.3 它的目标是最终收敛，不是即时传播

这点要说清楚。

full broadcast 追求快，partial fanout 追求便宜，anti-entropy 追求最后不漏。

它们三者的目标不一样，不能拿一个指标去要求全部。

---

## 5. 为什么 gossip 不能既当传播层又当一致性真相层

这是这篇最想说清楚的地方。

### 5.1 gossip 的第一职责是传播，不是裁判

gossip 最适合做的是：

- 把 view change 传出去
- 把 health 结论传出去
- 把 epoch 变化传出去

它的作用是“让更多节点知道”，不是“让更多节点自动同意”。

### 5.2 一旦让 gossip 当真相层，它就会反过来污染传播层

如果 gossip 既负责传播，又负责判定谁说了算，那它会自然膨胀成：

- 传播协议
- 共识协议
- 反熵协议
- 目录刷新协议
- 失效判定协议

这时候系统就没边界了。

### 5.3 真相层必须更窄

真相层应该只做这些事：

- 维护 membership 的当前版本
- 记录 epoch
- 保留 view change 历史
- 允许 policy 在明确证据下做最终动作

传播层只是把这些事实扩散出去。

如果传播层自己变成真相层，它一定会和 policy 打架。

### 5.4 对重建版 runtime 来说，最好把这两个角色彻底分开

更干净的切法是：

- `membership store / membership view` 负责真相
- `dissemination layer` 负责传播
- `anti-entropy` 负责补齐
- `relocation / routing / cache invalidation` 只消费事实，不自己发明事实

这样以后你换传播方式，不会动到真相模型。

---

## 6. 这三种传播方式怎么分工

如果把它们放在一条链上，大概是这样：

1. `probe` 触发或补充失败信号
2. `failure detector` 把信号整理成 health
3. `membership` 形成 view change 和 epoch 更新
4. `full broadcast` 在关键变更时快速扩散
5. `partial fanout` 在日常传播时降低成本
6. `anti-entropy` 在后台修补不一致和漏收

这个顺序很重要，因为它把“事实形成”和“事实扩散”拆开了。

---

## 7. 它们和前面四层主线怎么接

这篇不能单独看，要和前面的主线一起看。

### 7.1 和 probe / detector 的关系

probe 和 detector 负责把“节点有问题”这件事先看出来。

传播层不负责发现问题，它只负责把已经形成的事实传出去。

如果把传播层拿去发现问题，系统会非常绕。

### 7.2 和 stabilization 的关系

stabilization 的作用是控制传播之后的动作速度。

也就是说：

- 事实可以先传播
- 但动作不要立刻翻盘

比如：

- 一个节点刚刚 `Suspect`
- 不代表马上迁 owner
- 不代表马上清空所有 cache

传播层把事实扩散出去，stabilization 再决定哪些动作要等一等。

### 7.3 和 relocation 的关系

relocation 最怕的就是误触发。

所以它应该消费的是：

- 明确的 health 变化
- 已传播开的 view change
- 足够稳定的事实窗口

而不是某个节点刚收到的一条孤立信号。

这就是为什么传播层不能太躁，稳定层不能太虚。

---

## 8. 该继承哪些成熟经验

如果是重建版 runtime，我会保留这几条经验。

### 8.1 继承“按成本分层传播”

不是所有事实都值得全量广播。

这点很成熟，也很实用。

### 8.2 继承“传播和真相分离”

传播可以快，可以松，可以有损；真相必须窄，必须可追踪，必须带版本。

这条非常值得保留。

### 8.3 继承“最终要靠 anti-entropy 收口”

只靠一次广播，系统很难长期保持整洁。

后台补齐是必须的，而且应该被明确设计出来，而不是事后修 bug。

---

## 9. 应该避免哪些历史包袱

重建时，我会尽量避开这些坑。

### 9.1 避免把 gossip 写成万能胶

最怕的就是一个 gossip 层什么都接：

- 传播
- 校验
- 判决
- 路由
- 迁移

这会让所有上层都依赖它，最后谁都拆不掉。

### 9.2 避免把 full broadcast 当默认值

全量广播看起来省事，但它会把系统的成本、耦合和噪音一起放大。

默认值应该是更克制的 fanout。

### 9.3 避免把 anti-entropy 变成隐式补锅

anti-entropy 不能只是“偶尔后台扫一遍”。

它应该有明确策略：

- 怎么发现落后节点
- 怎么比较版本
- 怎么补齐缺失 view change
- 怎么避免重复修补

否则它就只是一个无名的后台任务。

---

## 10. 如果按重建版 runtime 来设计

我会把这一层拆成下面几块：

- `IMembershipViewStore`：保存当前 membership 真相和 epoch
- `IMembershipDissemination`：负责传播 view change 和 health facts
- `IPartialFanoutStrategy`：负责挑选传播对象
- `IAntiEntropyService`：负责补齐落后节点
- `IStabilizationPolicy`：负责控制传播后多久允许重动作

这样做的好处是：

- 传播策略可以换
- 反熵机制可以换
- 真相存储不会被传播逻辑污染
- relocation 和 routing 只消费结果，不参与制造结果

这比“先写一个大 gossip 服务”更干净。

---

## 11. 这篇话最想留下什么

一句话：

> full broadcast 负责快，partial fanout 负责省，anti-entropy 负责别漏，gossip 只负责把事实传出去，不负责替系统决定真相。

这条边界一旦立住，membership dissemination 才不会把 runtime 搅脏。
