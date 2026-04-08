# Membership Snapshots、Checkpoint 与 Restart Recovery：把恢复做快，但别把真相做假（第四十八篇）

这一篇承接前面的 [44-membership-health-and-relocation-policy.md](./44-membership-health-and-relocation-policy.md)、[45-membership-epochs-view-changes-and-failure-detection.md](./45-membership-epochs-view-changes-and-failure-detection.md)、[46-membership-probing-gossip-and-stabilization.md](./46-membership-probing-gossip-and-stabilization.md) 和 [47-membership-dissemination-and-anti-entropy.md](./47-membership-dissemination-and-anti-entropy.md)。

44 讲的是 `membership`、`node health`、`owner relocation policy` 怎么分。45 讲的是 `epoch`、`view change`、`failure detector` 怎么分。46 讲的是 `probe`、`gossip`、`stabilization` 怎么分。47 讲的是 `full broadcast`、`partial fanout`、`anti-entropy` 怎么分。

如果再往下走，就会碰到一个很现实的问题：

> 节点重启以后，怎么尽快恢复到“差不多知道当前集群长什么样”的状态，同时又不把旧快照当成真相？

这就是这一篇要讲的。

在重建版 runtime 里，我会把它拆成三层：

- `authoritative membership`
- `local membership view`
- `snapshot / checkpoint`

它们都和恢复有关，但职责完全不同。

如果这三层写成一坨，checkpoint 最后就会被当成真相来源，restart recovery 也会变成“拿旧状态硬顶新世界”。那样 membership 一定会写脏。

---

## 1. 先说结论

如果我要重建一版 Orleans，我会把这三层说死：

- `authoritative membership` 是真相源，负责当前成员视图到底是什么。
- `local membership view` 是节点本地持有的视图副本，负责快速查询和快速启动。
- `snapshot / checkpoint` 是恢复加速器，负责让重启节点少走几步，但它不是最终真相。

这三层可以互相配合，但不能互相代替。

如果混了，系统会立刻变形：

- snapshot 会被当成 authoritative truth
- local view 会被当成全局真相
- recovery 会被当成重新定义 membership 的机会

这不是实现细节，是重启恢复和成员管理的基本边界。

---

## 2. authoritative membership 到底是什么

authoritative membership 的角色很明确：它是当前成员事实的根。

### 2.1 它负责回答“现在到底算谁在集群里”

它至少要能回答这些问题：

- 当前有效成员有哪些
- 哪些节点已经退出视图
- 哪些节点只是暂时 suspect
- 当前 epoch 是多少
- 哪些 view change 已经正式生效

它关心的是“谁说了算”，不是“某个节点本地记了什么”。

### 2.2 它不能被 local view 反向定义

这点很重要。

节点本地重启时，可能只拿到：

- 一个旧 snapshot
- 一段本地 checkpoint
- 一小部分 gossip 传播过来的信息

这些都不能反过来定义 authoritative membership。

它们只能用来加速恢复。

### 2.3 它必须和 epoch 绑定

不管 membership 怎么存，authoritative truth 最好都带版本线：

- epoch
- view change history
- 当前生效原因

这样本地恢复时，才能判断：

- 我拿到的是不是旧世界的残片
- 这份 snapshot 对不对当前视图
- checkpoint 还能不能用

---

## 3. local membership view 是什么

local membership view 是节点自己手上的那份视图副本。

### 3.1 它是为了“快”

本地节点不应该每次查个成员状态都去问远端。

local view 的存在是为了：

- 快速判断一个节点是不是大概率可用
- 快速做 routing / cache lookup
- 快速恢复启动后的第一批请求

也就是说，它服务的是性能和启动速度。

### 3.2 它天生可能过期

local view 会过期，而且这是正常的。

因为它来自：

- gossip dissemination
- checkpoint 恢复
- 本地缓存
- 之前的 view change

这些来源都不是绝对实时的。

所以 local view 的正确姿势不是“绝不能错”，而是“错了以后能尽快修正”。

### 3.3 它不能越权

local view 再完整，也只是副本。

它不能：

- 自己升级成 authoritative membership
- 自己决定成员视图怎么改
- 自己把 checkpoint 里旧状态当最终结论

它只能消费事实，然后尽量快地把服务重新拉起来。

---

## 4. snapshot 和 checkpoint 到底干什么

这部分最容易被误解。

### 4.1 checkpoint 是恢复加速器

checkpoint 的作用只有一个：让节点重启后少从零开始。

它可以带来这些好处：

- 快速恢复 local membership view
- 快速恢复最近的 epoch / view change 记忆
- 快速恢复一些 gossip 传播缓存
- 快速进入可查询状态

它解决的是“恢复慢”的问题，不是“真相缺失”的问题。

### 4.2 checkpoint 不是真相来源

这一点必须说死。

checkpoint 可能是：

- 上一次正常运行时留下的快照
- 某个时刻的本地状态副本
- 一段传播过来的历史成员信息

但它都不是当前集群真相。

所以恢复时应该做的是：

1. 先用 checkpoint 快速起步
2. 再用 authoritative membership 对齐
3. 再靠 gossip / anti-entropy 把本地视图收敛到当前事实

而不是：

1. 直接信 checkpoint
2. 把它当成当前 membership
3. 用它重新定义集群

### 4.3 checkpoint 也不该承担一致性职责

checkpoint 适合做：

- 本地恢复加速
- 冷启动预热
- 短期故障后的快速回填

它不适合做：

- 全局真相同步
- view change 的裁决
- owner relocation 的依据

---

## 5. restart recovery 应该怎么走

重启恢复最容易写成“先读快照，再开机，再说”。这不够。

### 5.1 先恢复本地服务能力

节点重启后，最先要做的是：

- 让 runtime 能起来
- 让本地 membership view 能查询
- 让 probe / detector / dissemination 相关组件能跑起来

这时候 checkpoint 很有用，因为它能缩短冷启动。

### 5.2 再去对齐 authoritative membership

本地起来以后，马上要做的不是“宣布自己已经知道真相”，而是：

- 拉取或接收最新 membership view
- 比对 checkpoint 里的 epoch
- 识别哪些事实已经过期
- 丢弃已经不可信的本地结论

这一步很关键，因为它决定本地恢复是不是在旧世界里打转。

### 5.3 最后靠 dissemination 和 anti-entropy 收口

即便已经拿到了 authoritative membership，本地仍然可能漏掉一些传播事实。

所以还要靠：

- gossip dissemination
- partial fanout
- anti-entropy

把缺的 view change、health facts、epoch 变化慢慢补齐。

这时候 checkpoint 只是起点，不是终点。

---

## 6. 这和前面的 probe / detector / stabilization / anti-entropy 怎么接

这一篇不能单独看，它必须接前面那条主线。

### 6.1 和 probe / detector 的关系

重启恢复后，probe 和 detector 不能直接信 checkpoint。

它们应该做的是：

- 重新开始采样
- 重新建立 health 证据
- 重新判断本地节点和远端节点的状态

checkpoint 最多帮它们少走几步，不该替它们下结论。

### 6.2 和 stabilization 的关系

恢复期很容易误判。

比如：

- 节点刚起来
- 本地 view 还没完全对齐
- gossip 还没传播到位
- detector 还在积累证据

这时候如果立刻做激进动作，系统会抖。

所以 stabilization 仍然要在，尤其要给恢复后的短窗口留缓冲。

### 6.3 和 anti-entropy 的关系

anti-entropy 在这里特别重要，因为恢复节点最容易落后。

它要做的就是：

- 找出 checkpoint 和当前视图的差异
- 补齐缺失的 view change
- 重新拉平本地缓存
- 避免把旧 epoch 当成当前事实

如果没有 anti-entropy，checkpoint 只能让节点“先活过来”，不能让它“最后对齐上”。

---

## 7. 为什么 checkpoint 不能是真相来源

这一节要单独说，因为这是最容易写歪的地方。

### 7.1 checkpoint 本质上是历史

它记录的是：

- 某个时刻的本地状态
- 某个时刻的成员副本
- 某个时刻的传播缓存

历史能帮助恢复，但历史不能自动变成当前。

### 7.2 真相来源必须可更新、可传播、可校验

authoritative membership 至少要具备：

- 版本线
- 传播路径
- 对齐机制

checkpoint 只有“静态保存”，没有这些就不够。

### 7.3 把 checkpoint 当真相，会直接制造脏恢复

最典型的问题会是：

- 节点恢复后拿旧 owner 记录继续转发
- 节点恢复后把过期的健康状态当当前事实
- 节点恢复后拒绝接受新的 view change

这类 bug 都很难看，而且很难定位。

所以 checkpoint 必须明确退居二线。

---

## 8. 该继承哪些成熟经验

如果是重建版 runtime，我会保留这几条经验。

### 8.1 继承“本地缓存 + 全局对齐”

先快起来，再对齐，这条路径是对的。

### 8.2 继承“checkpoint 服务恢复，不服务真相”

这是最值钱的边界之一。

把 checkpoint 当恢复加速器，系统会很稳。

### 8.3 继承“恢复后必须重新验证”

重启节点不能假装自己还活在旧世界里。

它必须重新接入 probe、detector、dissemination、anti-entropy 的主线。

---

## 9. 应该避免哪些历史包袱

重建的时候，我会尽量避开这些坑。

### 9.1 避免把 snapshot 写成单机自洽真相

snapshot 能让节点看起来“像已经知道很多事了”，但这只是错觉。

它不该成为独立真相面。

### 9.2 避免恢复时过度相信本地状态

本地 checkpoint 和 local view 只能作为起点。

恢复流程必须有一个显式的对齐阶段。

### 9.3 避免把 checkpoint 和 anti-entropy 混成一件事

checkpoint 是静态起步，anti-entropy 是动态收口。

这两个职责不同，不能揉在一起。

---

## 10. 如果按重建版 runtime 来设计

我会把这一层拆成下面几块：

- `IAuthoritativeMembershipSource`：提供当前真相和 epoch
- `ILocalMembershipView`：保存节点本地可快速查询的副本
- `IMembershipCheckpointStore`：保存恢复用快照
- `IRestartRecoveryCoordinator`：负责 checkpoint 起步后去对齐真相
- `IAntiEntropyService`：负责把本地视图和当前事实补齐

这样做的好处很直接：

- checkpoint 不会越权
- local view 不会伪装成真相
- authoritative source 不会被恢复逻辑污染
- recovery 过程会很清楚

这比“重启时直接把一坨状态读回来”更干净。

---

## 11. 这篇话最想留下什么

一句话：

> checkpoint 负责把节点更快拉起来，authoritative membership 负责告诉它世界现在长什么样，local view 负责先让它能跑，anti-entropy 负责最后把它对齐。

只要这条边界立住，membership snapshots 和 restart recovery 就不会把 runtime 写脏。
