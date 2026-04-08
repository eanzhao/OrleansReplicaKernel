# Grain Directory State Checkpoint、Activation Metadata Recovery 与 Restart 后的重新落地（第四十九篇）

这一篇承接前面的 [47-membership-dissemination-and-anti-entropy.md](./47-membership-dissemination-and-anti-entropy.md) 和 [48-membership-snapshots-checkpoint-and-recovery.md](./48-membership-snapshots-checkpoint-and-recovery.md)。

47 讲的是 `gossip` 怎么把成员事实传开，但不把系统传乱。48 讲的是 `membership checkpoint` 怎么让节点重启后先恢复到一个“差不多知道当前集群长什么样”的状态。

如果再往下走，就会碰到一个更贴近运行时的数据面问题：

> membership 只是告诉你“谁大概在、谁大概不在”；那 grain directory 的 owner 记录、activation 的本地元数据、以及重启后的恢复动作，应该怎么分？

这就是这一篇要讲的。

我会把它拆成三层：

- `grain directory owner record`
- `activation metadata`
- `activation instance`

它们都和恢复有关，但职责完全不同。

如果这三层写成一坨，重启恢复最后就会变成两种坏味道之一：

- 只恢复了 membership，结果 directory 还是空的，所有 grain 都像第一次见面
- 试图把 activation 实例也一起恢复，结果把运行时对象、调度状态、异步调用链都当成 checkpoint 了

这两种都不对。

---

## 1. 先说结论

如果我要重建一版 Orleans，我会把这三层说死：

- `membership checkpoint` 负责恢复“集群现在大概长什么样”
- `grain directory checkpoint` 负责恢复“某个 grain 的 owner 记录和路由元数据”
- `activation metadata` 负责恢复“这个 grain 之前在本地是怎么活着的”
- `activation instance` 本身不能直接靠 checkpoint 复活，它只能在恢复后的新 runtime 里重新创建

这几层可以配合，但不能互相代替。

如果混了，系统会立刻变形：

- membership checkpoint 会被当成数据面恢复包
- directory owner record 会被当成 activation 实例
- activation metadata 会被误写成完整对象快照

这不是实现细节，是重建版 runtime 能不能干净的边界。

---

## 2. grain directory owner record 到底是什么

grain directory 的 owner record 是路由层的事实，不是对象层的事实。

### 2.1 它回答的是“这个 grain 该找谁”

它至少要能回答这些问题：

- 这个 grain 现在归哪个 node
- 这个 owner 记录的版本是多少
- 这个 owner 是如何迁移过来的
- 这个 owner 记录是否已经过期

它的职责很窄：告诉 runtime 调用该去哪儿。

### 2.2 它不是 activation

这一点必须说清楚。

owner record 只是一条路由记录，它不是：

- grain 实例
- scheduler
- mailbox
- 调度上下文
- 正在执行的 turn

所以 directory checkpoint 能保存的是 owner 记录和版本，不是对象状态。

### 2.3 它可以 checkpoint

这个记录很适合 checkpoint，因为它本来就是“可持久化的路由事实”。

恢复时你可以把它重新加载出来，然后让 router 继续知道：

- 哪些 grain 还在本地
- 哪些 grain 已经迁走
- 哪些 grain 该走远端转发

这会让重启后的第一批请求少走很多弯路。

---

## 3. activation metadata 到底是什么

activation metadata 是 activation 的本地影子，不是 activation 本身。

### 3.1 它记录的是“这个 activation 曾经怎么活着”

它可以包括这些东西：

- grain id
- owner node
- activation generation / incarnation
- 最近一次活动时间
- 是否正在执行
- 是否可以被 idle collection
- 是否已经被标记为 deactivating

这些都属于“生命周期元数据”。

### 3.2 它可以帮助恢复，但不能替代实例

activation metadata 的价值在于：

- 重启后不必从 0 推断这个 grain 以前是本地还是远端
- 可以更快决定要不要重建本地 activation
- 可以更快恢复 idle 统计、回收判断、调度边界

但它不能恢复：

- grain 对象的 CLR 实例
- 它内部的临时字段
- 运行中的异步任务
- 已经排队但没跑完的 turn

这些东西都不是 metadata 能安全重建的。

### 3.3 它是恢复加速器，不是真相本体

这和 membership checkpoint 的逻辑一样。

activation metadata 只能帮你：

- 快速把 runtime 拉回可服务状态
- 快速恢复“这个 grain 之前大概怎么跑”
- 快速决定第一批调用该落到哪儿

它不能：

- 自己定义业务状态
- 自己冒充对象状态
- 自己把旧对象重新塞回新 runtime

---

## 4. 为什么 activation 实例本身不能直接靠 checkpoint 复活

这是最关键的一层。

### 4.1 因为 activation 不是纯数据

activation 实例不是一个简单的 `record` 或 `DTO`。

它里面通常混着很多运行时状态：

- scheduler 句柄
- mailbox / queue
- 并发控制
- 正在执行的 task
- 取消信号
- 本地缓存
- 运行时服务引用

这些东西不是“存一下就能恢复”的对象。

### 4.2 因为你恢复的不是“对象”，而是“新的一次生命”

重启以后，哪怕 grain id 一样，activation 也应该被当成新的一次生命。

这意味着：

- 可以恢复路由意图
- 可以恢复元数据
- 可以恢复某些状态指针
- 但不能把旧对象原封不动塞回来

否则你会得到一个很危险的 runtime：

- 旧 scheduler 还在
- 旧 mailbox 还在
- 旧异步调用链还在
- 新 runtime 却以为自己已经接管完成

这会非常脏。

### 4.3 因为执行中的 turn 没法可靠重放

activation 里最难恢复的不是对象字段，而是“正在跑的活”。

如果 checkpoint 发生在这些时刻：

- 一半方法已经执行
- 一半状态已经更新
- 一半响应还没发出去

那你其实根本不知道该不该重放、该不该回滚、该不该继续。

所以重建版 runtime 里最稳妥的边界就是：

- checkpoint 只恢复元数据
- 实例重新创建
- 业务状态由独立存储或业务协议自己决定

这条线不能乱。

---

## 5. directory checkpoint 和 membership checkpoint 的关系

这两个东西关系很近，但不是一回事。

### 5.1 membership checkpoint 先决定“世界长什么样”

membership checkpoint 先恢复的是：

- 当前 epoch
- authoritative membership
- local membership view
- dissemination cursor

它先把“集群事实”恢复出来。

### 5.2 directory checkpoint 再决定“路由怎么走”

directory checkpoint 恢复的是：

- grain owner 记录
- owner version
- 最近的 relocation 结果
- 路由缓存里还能不能继续沿用某个地址

它依赖 membership checkpoint，但不是 membership checkpoint 的附属品。

### 5.3 先恢复 membership，再恢复 directory

顺序最好是这样：

1. 先用 membership checkpoint 恢复集群事实
2. 再让 directory 用这些事实恢复 owner 记录
3. 再用 owner 记录恢复 routing / cache / activation metadata 的落点

如果反过来，directory 很可能会把一个已经过期的 owner 当成当前事实。

---

## 6. activation metadata recovery 应该怎么做

这部分最容易被误写成“恢复 activation 对象”。

### 6.1 先恢复 metadata，再懒创建实例

更干净的流程应该是：

1. 恢复 activation metadata
2. 恢复 directory owner record
3. 等第一批调用真的到来时，再创建新的 activation instance

这样就不会把 checkpoint 变成对象复活器。

### 6.2 metadata 主要用于 three things

activation metadata 最值钱的地方通常是这三件事：

- 判断这个 grain 是否曾经在本地热过
- 判断它是不是刚刚被 deactivated
- 判断是否值得快速重建为本地 activation

它更像一个恢复提示器，不像一个对象备份器。

### 6.3 metadata 还可以帮助恢复调度边界

比如：

- 是否存在未完成的 deactivation
- 是否仍在 cooldown
- 是否应该允许 immediate recreate

这些都属于 metadata 能管的范围。

但它还是不能代替 activation 实例本身。

---

## 7. 这和前面的 membership / dissemination / checkpoint 主线怎么接

这一篇不能单独看，它必须接前面的主线。

### 7.1 和 membership checkpoint 的关系

membership checkpoint 先把“集群事实”恢复出来。

directory checkpoint 和 activation metadata 只是把“本地服务恢复”往前推了一步。

也就是说：

- membership 负责全局事实
- directory 负责路由事实
- activation metadata 负责本地生命周期事实

三者一层比一层窄。

### 7.2 和 dissemination / anti-entropy 的关系

即便有 checkpoint，重启节点也不能停在本地快照上。

它还需要：

- gossip dissemination
- partial fanout
- anti-entropy

把本地恢复出来的 directory 和 metadata 再次校准到当前事实。

checkpoint 只是加速，不是终局。

### 7.3 和 probe / detector / stabilization 的关系

重启以后，节点还要重新进入健康探测流程。

也就是说：

- probe 重新采样
- detector 重新整理信号
- stabilization 决定是否真的翻 stable 状态

directory checkpoint 和 activation metadata 不能跳过这条线。

它们最多帮 runtime 在恢复早期别完全空转。

---

## 8. 我会怎么切第一版实现

如果是重建版 runtime，我会把第一版切成这个顺序：

1. 先恢复 membership checkpoint
2. 再恢复 grain directory owner record
3. 再恢复 activation metadata
4. 最后 lazy recreate activation instance

这样边界最干净。

### 8.1 目录层先活

directory 恢复后，routing 至少能知道：

- 哪些 grain 该走本地
- 哪些 grain 该走远端
- 哪些 owner 记录要等校准

### 8.2 activation 层再慢慢起来

activation metadata 恢复后，runtime 能更聪明地决定：

- 是不是要立即重建
- 是不是先等等
- 是不是应该先让 probe / gossip 过一轮

### 8.3 真正实例最后再创建

等第一次调用来了，再创建新的 activation instance。

这样最稳，也最符合重建版 runtime 的边界。

---

## 9. 这版故意不做的事

- 不把 activation 实例序列化成 checkpoint
- 不把 scheduler、mailbox、异步任务一起恢复
- 不把 directory checkpoint 当成 membership truth
- 不把 metadata 当成业务状态
- 不把 checkpoint 当成重放机制

这篇只想把 `grain directory owner record`、`activation metadata`、`activation instance` 这三层拆开，并且让它们在一个很小的恢复模型里各归其位。

