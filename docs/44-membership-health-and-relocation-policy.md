# Membership、Node Health 与 Owner Relocation Policy：这三件事不能混着写（第四十四篇）

这一篇承接前面的 [40-single-node-to-multi-node-plan.md](./40-single-node-to-multi-node-plan.md)、[41-distributed-directory-and-owner-model.md](./41-distributed-directory-and-owner-model.md)、[42-remote-forwarding-and-message-loop.md](./42-remote-forwarding-and-message-loop.md) 和 [43-failure-paths-timeouts-and-retries.md](./43-failure-paths-timeouts-and-retries.md)。

40 讲的是怎么从单节点 toy 走向多节点。41 讲的是 `directory / owner / locator` 怎么切。42 讲的是 `message loop` 和远端转发。43 讲的是失败链、超时、重试和 invalidation 怎么分层。

但如果再往下走，有一层还是很容易写脏：

> 节点是否在成员视图里、节点当前是否健康、某次调用是否失败，这三件事根本不是一回事。

如果这三件事混了，后面 owner relocation policy 也一定会跟着脏掉。Orleans 里很多“看起来像调度问题”的东西，最后其实都卡在这里。

---

## 1. 先说结论

如果我要重建一版 Orleans，我会把这三层说死：

- `membership` 提供的是“节点集合”和“节点身份是否仍然有效”的事实。
- `node health` 提供的是“这个节点此刻能不能被当成可用 owner / 可用转发目标”的事实。
- `owner relocation policy` 提供的是“当节点不健康或成员视图变化时，要不要把 grain owner 迁到别的节点”的决策。

这三层可以串起来，但不能互相吞职责。

如果混了，系统会立刻变形：

- membership 会被当成健康监控
- health 会被当成调用失败判断
- relocation 会被塞进 routing
- call failure 会被误写成 owner 迁移

这不是小问题，是后面所有分布式复杂度的起点。

---

## 2. membership 到底提供什么

membership 提供的是“成员视图”，不是“运行时健康结论”。

### 2.1 membership 是节点事实层

membership 至少要能回答：

- 当前有哪些节点在集群里
- 哪些节点已经退出成员视图
- 哪些节点的身份已经过期
- 当前这轮成员视图的 epoch 是什么

它关心的是“系统里还有谁”，不是“某个调用刚刚成没成功”。

### 2.2 membership 不是 call-level 信号

一次调用失败，不能直接推导出节点已经离开成员视图。

原因很简单：

- 请求可能只是超时
- transport 可能只是抖了一下
- 远端 grain 可能刚好抛错
- 节点本身也许还活着

所以 membership 不应该被一次调用失败直接改写。

### 2.3 membership 也不是 health 监控本身

membership 更像“谁是集合里的成员”，health 更像“这个成员此刻能不能用”。

前者偏稳定，后者偏实时。

如果把 health 直接塞进 membership，membership 就会变成一个不停抖的状态机，目录和路由都会跟着乱。

---

## 3. node health 和 transport failure 的关系

这两个东西有关，但不能等同。

### 3.1 transport failure 只是一个观察信号

transport failure 说明的是：

- 这次请求没送到
- 或者回包没回来
- 或者远端没法接收

它只是“当前这条消息链路失败了”的证据，不是“节点永久坏了”的结论。

### 3.2 node health 是更高一层的判断

node health 需要综合更多信号：

- transport failure 频率
- 心跳是否超时
- 是否还在 membership 里
- 是否连续多次不可达

也就是说，transport failure 是输入，health 是输出。

### 3.3 单次失败不能直接判死节点

这点要说死。

如果一次 remote call 没送到，就直接把节点踢出成员视图，系统会非常脆：

- 一次短暂抖动就会触发 owner 重分配
- routing 会一直抖
- activation 会被反复 shed

所以正确做法通常是：

- 先记录 failure
- 更新 node health
- 再由 policy 决定要不要 relocation

---

## 4. owner relocation policy 应该挂在哪层

这个 policy 不能挂在 routing，也不能挂在 directory 的底层缓存里。

### 4.1 不要挂在 routing

routing 的职责是：

- 看 owner
- 决定本地还是远端
- 决定下一跳

它不该顺手决定“owner 要不要迁走”。

一旦 routing 开始管 relocation，它就会把：

- 定位
- 转发
- 失效
- 迁移

揉成一个大块，后面一定会脏。

### 4.2 不要挂在 directory 的最底层

directory 的职责是保存 owner 语义，处理失效和重新定位。

它不应该自己决定“策略上要不要迁”。

更合理的是：

- directory 维护 owner 记录
- health 提供节点可用性事实
- relocation policy 决定要不要改 owner

### 4.3 正确位置是“目录之上、宿主之下”

更像这样：

- `membership` 提供成员集合
- `health` 提供节点当前可用性
- `owner relocation policy` 消费 membership + health
- `directory` 落 owner 记录
- `locator / routing` 消费 directory

这样 policy 既看得到全局事实，又不会直接吞掉执行层。

---

## 5. 什么时候允许自动迁移 owner

这个问题最重要，因为它决定系统会不会乱迁。

### 5.1 允许自动迁移的情况

我会把自动迁移限制得比较保守，只在这些情况下考虑：

- 节点已经不在 membership 里
- 节点被明确判定为 hard down
- owner 记录已经失效，且可以安全地重新分配
- 迁移不会打断已有的强语义流程

也就是说，自动迁移应该偏向“明确不可用”而不是“可能慢了一点”。

### 5.2 不允许自动迁移的情况

下面这些情况，通常只该做 invalidation，不该直接迁 owner：

- 单次 transport failure
- 单次调用超时
- 单个 response error
- 临时网络抖动
- 还没有足够证据说明节点真的坏了

这些信号只能说明“这次调用出了问题”，不能说明“owner 该换人了”。

### 5.3 自动迁移要有代价意识

owner 不是免费换的。

一旦自动迁移过于激进，就会出现：

- activation 被频繁 shed
- cache 一直失效
- routing 一直重算
- 调用延迟被放大

所以 relocation policy 本质上是个“节流阀”，不是“看到失败就搬家”的开关。

---

## 6. 什么时候只能 invalidation，不迁移

这条线要单独拎出来，不然很容易把失败都写成 owner 迁移。

### 6.1 只 invalidation 的典型场景

- locator 缓存命中了旧地址
- transport 返回一次失败
- response 带回执行异常
- timeout 触发了重试

这些情况里，最合理的第一步通常是：

- 让旧地址失效
- 重新定位
- 重新判断 owner 是否真的需要变

### 6.2 invalidation 解决的是“旧事实不再可信”

它不是“把 owner 立刻改掉”。

它的作用是：

- 让后续调用不要继续盲信旧地址
- 给 policy 一个重新判断的机会

这比直接迁移更稳。

---

## 7. 三个概念怎么分开

这个地方必须说清楚。

### 7.1 节点是否在成员视图里

这是 membership 问题。

它回答的是：这个节点算不算系统成员。

### 7.2 节点当前是否健康

这是 health 问题。

它回答的是：这个节点此刻还能不能被当成可用目标。

### 7.3 某次调用是否失败

这是 invocation / message loop 问题。

它回答的是：这一次请求有没有成功完成。

这三件事的关系不是一条线，而是三层：

- call failure 可以影响 health
- health 可以影响 relocation
- relocation 可以影响 directory
- directory 再影响 routing

但它们不能反过来乱跳。

---

## 8. 如果重建一版，该怎么切

如果从头来，我会把这条线切成 5 个抽象。

### 8.1 Membership view

只管成员集合和 epoch。

### 8.2 Node health

只管节点当前是否可用。

### 8.3 Relocation policy

只管什么时候该迁 owner，什么时候只做 invalidation。

### 8.4 Distributed directory

只管 owner 记录和定位结果。

### 8.5 Local activation directory

只管本节点 activation 的创建、复用、卸载和回收。

这样切以后，系统的判断链会比较干净：

`call failure -> health update -> policy decision -> directory invalidation / owner relocation -> routing -> local activation`

而不是把所有事都塞进一个“大目录”里。

---

## 9. 对现在这条路线的判断

如果要继续从 toy 往前推，我的建议很明确：

- membership 先只做节点集合和可见性
- health 先只做可用 / 不可用
- relocation policy 先只做保守迁移
- 不要把一次失败直接当成 owner 改写
- 不要把 health 和 directory 混成一个对象

这一步看起来克制，但其实很值。

因为只有把 membership、health、relocation 这三层拆开，后面真正进多节点时，我们才不会又把目录、路由、容错和调度重新搅成一锅。
