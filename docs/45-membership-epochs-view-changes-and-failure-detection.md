# Membership Epoch、View Change 与 Failure Detection：成员视图怎么不写脏（第四十五篇）

这一篇承接前面的 [44-membership-health-and-relocation-policy.md](./44-membership-health-and-relocation-policy.md)。

44 讲的是 `membership`、`node health`、`owner relocation policy` 这三层怎么分。可如果再往下走，会遇到一个更细的点：

> 成员视图为什么需要 `epoch`，`view change` 应该记录什么，`failure detector` 到底提供什么证据。

如果这件事不先说清楚，membership 最后就会被写成一坨：既像健康监控，又像成员管理，又像故障转移日志。那样后面一加分布式复杂度，整个系统还是会往脏里滑。

---

## 1. 先说结论

如果我要重建一版 Orleans，我会把这三件事说死：

- `membership epoch` 用来标记“这一轮成员视图是谁说了算”。
- `view change` 用来记录“成员视图从哪一版变到哪一版，改了什么”。
- `failure detector` 用来提供“某个节点现在不太可信”的证据，但它自己不直接等于视图变化。

这三层可以串起来，但不能混成一层。

如果混了，membership 就会开始背所有锅：

- 瞬时失败会被记成成员变更
- 健康波动会被记成 view change
- 节点不可达会被记成 owner 迁移的直接指令

这不是小问题，是后面所有 membership 复杂度的起点。

---

## 2. membership epoch 到底是干什么的

epoch 不是一个装饰字段，它是成员视图的边界。

### 2.1 epoch 用来区分“这是哪一轮视图”

当系统进入多节点以后，成员视图会变化：

- 节点加入
- 节点退出
- 节点被踢出
- 节点恢复

如果没有 epoch，你就很难判断：

- 现在这个成员表是不是过期的
- 某个 health 结论是在哪一轮视图里产生的
- 一个 relocation 决策是不是基于旧成员表做的

所以 epoch 的首要作用不是“编号好看”，而是把视图切片。

### 2.2 epoch 让旧事实失效变得可见

有了 epoch，后面的组件就能知道：

- 这条 owner 记录是基于哪一轮成员视图算出来的
- 这个 locator cache 是在哪一轮被确认的
- 这个 health 结论是否还适用于当前成员视图

换句话说，epoch 让“旧视图里的正确”不再被误当成“当前仍然正确”。

### 2.3 epoch 不等于时间戳

这点要说清楚。

epoch 不是“某个时刻”，而是“某个视图版本”。

时间戳只能告诉你早晚，epoch 告诉你视图关系：

- 这个判断基于哪一轮成员表
- 后面有没有新的 view change 把它推翻

对 membership 来说，后者比前者更重要。

---

## 3. view change 应该记录什么

view change 不是简单的“写一条日志”，它应该是成员视图发生变化时的最小事实单元。

### 3.1 view change 至少要能表达版本跳变

最少要记录：

- 旧 epoch
- 新 epoch
- 变更原因
- 受影响的节点集合

这样后面的 routing、directory、health 才能知道：

- 哪些 cache 要失效
- 哪些 owner 记录可能要重算
- 哪些健康结论已经不该继续沿用

### 3.2 view change 不是 call-level 事件

一次调用失败，不应该直接生成 view change。

view change 是“成员视图变了”，不是“某次调用没成功”。

这两个概念不能混：

- 调用失败是瞬时信号
- view change 是成员事实变化

如果把调用失败直接写成 view change，membership 就会变得极其敏感，系统会一直抖。

### 3.3 view change 也不是 owner relocation 本身

view change 可以触发 owner relocation，但不等于 relocation。

更准确地说：

- view change 改变了“谁还算成员”
- relocation policy 决定“谁该接管 owner”
- directory 落下最终 owner 记录

这三件事顺序不能反。

---

## 4. failure detector 提供的到底是什么

failure detector 最容易被误解成“判死机的工具”，其实它更像一个证据收集器。

### 4.1 failure detector 不直接改 membership

failure detector 的输入通常是：

- 心跳超时
- transport failure
- response delay
- 连续不可达
- 节点侧 ack 缺失

这些都只是证据，不是结论。

failure detector 负责把这些证据汇总成：

- `suspect`
- `unhealthy`
- `probable failure`

但它不应该自己直接把节点从 membership 里删掉。

### 4.2 failure detector 也不等于 transport failure

transport failure 只是一次消息没送到、或者回不来。

failure detector 需要看的是趋势：

- 是不是连续发生
- 是不是多个方向都失败
- 是不是和心跳一起出现
- 是不是已经超过阈值

所以 transport failure 是输入，failure detector 是证据整理层。

### 4.3 failure detector 的输出应该是状态，不是动作

输出最好是：

- `Healthy`
- `Suspect`
- `Unhealthy`
- `Dead`

而不是：

- 立刻迁 owner
- 立刻删成员
- 立刻重建目录

动作应该由上层 policy 来做。

---

## 5. view change 和 node health 的关系

这两层关系紧，但不是一回事。

### 5.1 health 是局部事实，view change 是全局事实

node health 说的是：

- 这个节点此刻能不能用
- 能不能继续当 owner
- 能不能继续当转发目标

view change 说的是：

- 成员视图有没有改
- 这一轮成员表是不是更新了
- 某些节点是否已经退出或被剔除

health 可以变很多次，但 view change 不一定每次都跟着变。

### 5.2 health 变化可以先于 view change

一个节点先被判为 `Suspect` 或 `Unhealthy`，并不意味着它马上从成员视图里消失。

这通常是合理的：

- 先记录 health 变化
- 再积累证据
- 证据足够后再升级成 view change

这样系统不会因为短暂抖动就频繁改视图。

### 5.3 view change 之后 health 要重新解释

一旦 view change 发生，旧的 health 结论就不能无限沿用。

因为：

- 成员集合变了
- epoch 变了
- 旧 cache 可能已经不可信

所以 health 不是孤立存在的，它总要绑在某一轮 view 上看。

---

## 6. 什么时候只更新 health

这条线要单独拎出来，不然系统很容易过度反应。

### 6.1 只更新 health 的典型场景

- 一次 transport failure
- 一次 remote timeout
- 一次 response error
- 短时间内的几次抖动，但还没跨过阈值

这些情况里，最合理的动作通常是：

- 标记 health 变化
- 记下 failure 证据
- 不立即改 membership view

### 6.2 只更新 health 的意义

这样做的好处是：

- 不会让成员视图跟着请求失败一起抖
- 不会让 owner relocation 过早发生
- 不会把短暂问题放大成集群变更

health 是缓冲层，不是最终判决。

---

## 7. 什么时候升级成 view change

view change 不是每次失败都该发生，它应该建立在更强的证据上。

### 7.1 适合升级成 view change 的情况

- 节点明确下线
- 节点长时间不可达
- failure detector 连续给出强证据
- membership 协议已经确认这一轮视图需要更新

### 7.2 不适合升级成 view change 的情况

- 单次调用失败
- 单次 timeout
- 一次 transport 失败
- 一次 response error
- 短暂网络抖动

这些情况最多说明 health 变了，不说明 membership 必须变。

### 7.3 view change 的代价

一旦升级成 view change，后面通常会连带触发：

- locator cache 失效
- owner 记录重算
- relocation policy 重新选择
- routing 重新判断

所以 view change 是重动作，不能轻易发。

---

## 8. 如果重建一版，该怎么切

如果从头来，我会把这条线切成 4 个抽象。

### 8.1 Membership view

只管：

- 当前成员集合
- epoch
- view change 记录

### 8.2 Failure detector

只管：

- 收集心跳和 transport 失败证据
- 产出 suspect / unhealthy 之类状态
- 不直接改 owner

### 8.3 Health evaluator

只管：

- 把 failure detector 的证据翻译成 node health
- 判断某个节点此刻是否可用
- 为 relocation policy 提供输入

### 8.4 Relocation / policy

只管：

- 什么时候该升级到 view change
- 什么时候该迁 owner
- 迁到哪个健康节点

这样一来，membership、health、failure detection、relocation 就能各管一层，不会互相吞。

---

## 9. 最后一句

如果把这一层写对了，后面很多看起来像“路由问题”“容错问题”“调度问题”的东西，才能老老实实待在自己的层里。

如果把这一层写歪了，系统最后就会变成：

- 调用失败在改成员视图
- health 在改 owner
- failure detector 在替代 membership
- routing 在替代 policy

那样再多的抽象，也只是把脏东西包得更严实。
