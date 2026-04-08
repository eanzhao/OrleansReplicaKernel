# Placement Policy、Activation Rebalancing 与 Handoff：先放哪儿、什么时候搬、搬的时候怎么收口（第五十篇）

这一篇承接前面的 [49-directory-state-checkpoint-and-activation-recovery.md](./49-directory-state-checkpoint-and-activation-recovery.md)。

49 讲的是重启恢复：membership、directory、activation metadata 怎么先把“运行时长什么样”拉回来。

如果再往前看，就会碰到另一类更贴近运行时热路径的问题：

> 一个 grain 最开始应该放在哪个 node，上线后如果负载不均衡该不该搬，搬的时候旧 activation 和新 activation 又怎么交接？

这就是这一篇要讲的。

我会把它拆成三层：

- `initial placement`
- `load-aware rebalancing`
- `handoff`

它们都和 placement 有关，但职责完全不同。

如果这三层写成一坨，placement policy 很快就会膨胀成一个小型 runtime，最后把 directory、membership、activation lifecycle 全吞进去。

这不是“功能更全”，而是“边界更脏”。

---

## 1. 先说结论

如果我要重建一版 Orleans，我会把 placement 这条线说死：

- `initial placement` 负责“一个新 grain 第一次该放哪儿”
- `load-aware rebalancing` 负责“这个 grain 现在放得合不合理，要不要迁”
- `handoff` 负责“迁的时候旧实例怎么收，新实例怎么接”

这三层可以串起来，但不能互相吞职责。

尤其是 placement policy 本身，应该尽量做成一个纯决策层：

- 输入是稳定的 membership 视图、directory 事实和负载信号
- 输出是一个目标 node，或者一个 rebalance 建议
- 它不负责真正把 activation 搬过去

真正的搬运动作，应该交给 handoff 或 lifecycle 协调层去做。

---

## 2. initial placement 到底在做什么

initial placement 只回答一个问题：

> 一个从来没落过点的 grain，第一站该去哪里？

### 2.1 它是“第一次落点”，不是“永久归宿”

initial placement 关心的是第一次启动成本最小、路由最合理：

- 选一个健康 node
- 尽量避开已经很忙的 node
- 尽量让同一类 grain 有可预测的分布
- 尽量别让 directory 一开始就乱跳

它不应该假设：

- 这个 grain 以后永远在这里
- 这个 node 永远是最优解
- 目录里的 owner 记录是不可变的

### 2.2 它应该依赖稳定输入

initial placement 不该直接盯着瞬时噪音做决定。

更合理的输入是：

- 稳定后的 membership view
- 节点健康状态
- 粗粒度负载信号
- 可能还有 grain 类型、key hash、affinity 约束

它不需要知道：

- activation 内部怎么调度
- owner 迁移的历史细节
- 这个 grain 之前有没有执行过

这些都不属于 initial placement。

### 2.3 它和 directory 的关系

initial placement 的结果最终要写进 directory，但它本身不是 directory。

directory 负责：

- 记住 owner
- 做版本化
- 做缓存失效
- 让后续调用能找到目标

placement policy 只负责：

- 提供一个初始 owner 建议

这条边界很重要。

---

## 3. load-aware rebalancing 到底在做什么

rebalancing 只回答一个问题：

> 这个 grain 现在放得还合适吗？

### 3.1 它不是 placement 的另一个名字

initial placement 是“第一次放”。

rebalancing 是“后面再看，放得均不均衡，需不需要挪”。

这两件事可以共用一些打分逻辑，但不能共用一个职责模型。

如果不分开，系统最后会变成：

- 新 grain 一创建就被当成可重平衡对象
- 目录里的 owner 记录每次都像新的一样被重新算
- runtime 永远在做“重新挑位置”，没有稳定期

这会非常吵。

### 3.2 它应该看什么

load-aware rebalancing 更适合看这些信号：

- 当前 node 的负载
- activation 数量
- 调度队列长度
- 最近的请求密度
- 某类 grain 是否明显偏热

它看的是“运行态分布”，不是“成员事实”。

membership 告诉你 node 能不能用，rebalancing 告诉你 node 用得匀不匀。

这俩不能混。

### 3.3 它输出的也不该是动作

rebalancing 最好只输出：

- `keep`
- `move`
- `move to node X`
- `delay`

真正怎么搬、搬到哪一步停、旧实例怎么退出，应该交给 handoff。

这样 policy 就只是 policy，不会自己变成一个半个 runtime。

---

## 4. handoff 到底在做什么

handoff 是最容易被误写成“迁移 = 把对象 copy 过去”的一层。

实际上它应该是一个执行协议。

### 4.1 handoff 不是 placement

placement 负责决定“去哪儿”。

handoff 负责解决“怎么过去、怎么收尾”。

它至少要处理这些事：

- 旧 owner 是否还接受新调用
- 新 owner 什么时候开始接管
- 目录什么时候切 owner
- 旧 activation 什么时候退出
- 迁移过程中请求是重试、排队还是拒绝

### 4.2 handoff 不是 activation 复活

这点和前面的 checkpoint 一样重要。

handoff 可以做的是：

- 把路由切过去
- 把元数据传过去
- 把生命周期状态协调过去
- 把必要的状态同步过去

它不能做的是：

- 直接把旧 activation 实例原封不动复制过去
- 把 scheduler 和 mailbox 一起搬过去
- 把正在执行的 turn 当成可透明迁移对象

如果真这么做，runtime 会变得很难收口。

### 4.3 handoff 应该和 directory 配合，而不是吞掉 directory

directory 负责记录 owner。

handoff 负责把 owner 从旧位置切到新位置，并协调旧实例的退出。

这两者必须分开，因为：

- directory 需要稳定、可查询、可版本化
- handoff 需要动态、过程化、可失败

一个是事实层，一个是动作层。

---

## 5. 为什么 placement policy 不能吞掉 directory、membership、lifecycle

这是一条很关键的边界。

### 5.1 它不能吞 directory

因为 directory 管的是“当前事实”：

- 谁是 owner
- owner 版本是多少
- 当前地址是什么

placement policy 只管“建议放哪儿”，不该替 directory 保存事实。

否则 policy 一旦变复杂，就会把路由状态、缓存状态、重平衡状态混成一锅。

### 5.2 它不能吞 membership

因为 membership 管的是“这个 node 还算不算集群里的人”。

placement policy 只能使用 membership 作为输入，不该自己判断 membership 真伪。

否则它会变成：

- 既负责挑 node
- 又负责判断 node 活没活
- 还负责决定什么时候迁移

那就不是 policy 了，是一个小型集群控制平面。

### 5.3 它不能吞 activation lifecycle

因为 activation lifecycle 管的是：

- 创建
- 运行
- idle
- deactivation
- 重建

placement policy 不该知道这些对象什么时候真正被 dispose。

它只要知道：

- 这里是不是合适
- 要不要把下一个 activation 放到别处

真正的 lifecycle 细节，应该由 runtime 和 activation directory 处理。

---

## 6. 和 membership / directory / checkpoint 主线怎么接

这条线其实是一条很顺的链：

1. membership 先告诉你哪些 node 是稳定可用的
2. directory 记录 grain 当前 owner
3. checkpoint 让你在 restart 之后快速恢复这两层事实
4. placement policy 在稳定事实之上决定初始落点和重平衡建议
5. handoff 负责把 owner 真正切过去，并协调旧 activation 退出

也就是说：

- membership 是“地基”
- directory 是“路由事实”
- checkpoint 是“恢复加速器”
- placement policy 是“选址器”
- handoff 是“搬迁协议”

它们可以协作，但不能互相替代。

特别是 checkpoint 这层，它的作用只是让系统重启以后更快恢复到一个稳定起点。

它不能替代 placement policy，更不能替代 handoff。

---

## 7. 如果重建，第一版应该怎么切

如果我真要从头重建，我会把第一版 placement 设计成这样：

- initial placement 只做健康节点挑选和简单 hash / affinity
- rebalancing 只产出建议，不直接改 directory
- handoff 只负责 owner 切换和旧 activation 的有序退出
- membership / directory / checkpoint 都只是输入，不直接被 placement policy 改写

这样做的好处是：

- 边界清楚
- 便于测试
- 便于替换策略
- 便于以后把 rebalancing 做成独立后台任务

最重要的是，不会把 placement policy 写成一个混着事实、动作、生命周期的黑盒。

---

## 8. 这一篇真正想保留的东西

如果只保留一句话，那就是：

> initial placement 选位置，rebalancing 说要不要搬，handoff 负责真的搬；directory 记事实，membership 管集群，checkpoint 只负责让重启别从零开始。

这条边界守住了，后面的 runtime 才不会长成一团。

