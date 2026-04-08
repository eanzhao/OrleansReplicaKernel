# 从设计总纲到第一版实现计划：重建 Orleans 的第一步怎么走（第三十九篇）

这一篇承接前面的 [38-replica-design-blueprint.md](./38-replica-design-blueprint.md)，但不再重复蓝图本身。

38 讲的是“如果重建一版 Orleans，边界应该怎么切”。这一篇只回答更实在的一件事：

> 既然边界已经想清楚了，第一版到底先做什么，怎么做，做到什么程度算站稳。

我先把立场压住：

- 第一版不是完整替身，而是可验证的 runtime 骨架。
- 第一版不是把 Orleans 的能力都搬过来，而是把主干跑稳。
- 第一版不是先求可扩展，而是先求边界正确。
- 第一版不是为了“看起来像 Orleans”，而是为了“后面真的能长成一套新系统”。

如果这个阶段就贪多，最后大概率还是会回到旧系统那种“能用，但层次不干净”的路子。

---

## 1. 第一版目标边界

第一版只保留三件事。

### 1.1 先把主干跑通

最小主干是：

- `GetGrain`
- 强类型引用
- 调用协议对象
- 本地路由
- activation 创建与复用
- turn-based 调度
- 方法返回

这条链必须是真跑通，不是纸面上的抽象。

### 1.2 先做单节点内核

第一版只做单节点，不做 membership，不做跨 silo 路由，不做故障转移，不做 gateway 集群协同。

原因不是保守，而是顺序问题。单节点都没站稳，后面的分布式能力只会把错误放大。

### 1.3 先把边界固定住

第一版必须把这些边界固定下来：

- Identity
- Invocation
- Serialization
- Routing
- Activation
- Scheduling
- Host Composition

这几个层次不是“以后再说”的命名，而是后面所有能力的挂载点。

---

## 2. 明确不做什么

第一版要主动放掉一批能力，不然边界会糊。

### 2.1 不做的运行时能力

- membership
- 多集群
- gateway 负载均衡
- streams
- transactions
- persistent state
- timer / reminder
- security / auth
- diagnostics 全家桶

### 2.2 不做的兼容负担

- 不兼容 Orleans 老 serializer 格式
- 不兼容老 grain id 细节
- 不兼容老 provider 自动发现方式
- 不兼容历史代码生成遗留层
- 不兼容旧目录结构

### 2.3 不做的架构动作

- 不把所有能力塞进一个大 runtime 类
- 不让默认装配变成隐式魔法
- 不靠运行时扫描去“猜”系统长什么样
- 不在核心里硬塞插件生态

这不是偷懒，是把第一版从一开始就从旧包袱里剥出来。

---

## 3. 模块落地顺序

我会把第一版拆成 5 个阶段。

### 3.1 阶段一：Identity 和 Invocation

先把“这是谁”和“怎么发起一次调用”做实。

要落地的东西：

- `GrainId`
- `GrainAddress`
- `IInvokable`
- `IInvocationRuntime`
- 强类型引用壳
- 调用消息对象

验收标准：

- 能从 `GetGrain` 拿到稳定引用
- 能把一次方法调用打包成明确的 invocation 对象
- 能在日志里看见调用目标和请求 id

### 3.2 阶段二：本地路由和 activation

把“去哪儿执行”和“执行体怎么创建”分开。

要落地的东西：

- 本地 locator
- 本地 router
- activation directory
- activation entry
- activation 的创建、复用、释放

验收标准：

- 第一次调用会创建 activation
- 第二次调用会复用同一个 activation
- runtime 不再自己持有所有逻辑

### 3.3 阶段三：Scheduling

把 activation 上的 turn-based 语义做正确。

要落地的东西：

- 单 activation 串行队列
- turn 开始、结束、异常收口
- 后续再考虑 reentrancy / interleaving

验收标准：

- 同一个 activation 上的调用不会并发乱序执行
- 调度层能独立于 activation 和 routing 运行

### 3.4 阶段四：Host Composition

把 runtime 的装配方式固定住。

要落地的东西：

- builder
- registration
- host 生命周期
- demo 启动入口

验收标准：

- 外部仍然能通过 `Build` 和 `GetGrain` 使用系统
- runtime 的依赖图可以从 host 一眼看明白

### 3.5 阶段五：Codegen 占位层

这一步不是做完整编译器，而是把生成产物的边界先立住。

要落地的东西：

- generated reference
- generated invokable
- 生成物和手写壳的分离
- 后续接 source generator 的接口预留

验收标准：

- 业务代码不再直接手搓调用桩
- 生成产物的文件形状已经像真正 codegen 输出

---

## 4. 每个阶段的验收标准

第一版最怕的不是没做完，而是做完了也不知道算不算对。

所以验收标准要写死。

### 4.1 行为验收

- 一个 grain 类型能调用通
- 两个 grain 类型能复用同一套 runtime
- 同一 grain 的第二次调用能命中同一 activation
- turn scheduler 能按顺序处理调用

### 4.2 结构验收

- runtime 不直接持有所有职责
- routing 不知道业务对象怎么序列化
- activation 不知道集群怎么找自己
- codegen 产物不混进业务实现

### 4.3 演示验收

至少要有一个最小 demo 能打印出这条链：

`GetGrain -> reference -> invokable -> runtime -> router -> directory -> activation -> scheduler -> result`

只要这条链没通，后面的能力都不该往上堆。

---

## 5. 为什么先做单节点

这不是“先偷懒，后补分布式”，而是因为单节点是唯一能把语义看清楚的地方。

### 5.1 单节点能验证什么

- identity 是否稳定
- invocation 协议是否干净
- activation 边界是否合理
- scheduler 是否真的串行
- host 装配是否清楚

### 5.2 单节点看不清什么

如果一开始就上分布式，你会同时被这些东西干扰：

- membership
- 缓存失效
- placement 策略
- 消息重试
- 失败恢复
- gateway 选路

这些不是不能做，而是第一版不该让它们来污染主干。

### 5.3 单节点的价值

单节点先站稳之后，后面加分布式能力时，你才知道哪些问题是路由问题，哪些问题是调度问题，哪些问题是 activation 生命周期问题。

不然所有错误都会混在一起。

---

## 6. 代码结构建议

如果是从现在这个 toy 往真正 runtime 走，我建议分成三层仓库形状。

### 6.1 第一层：核心库

建议单独放一个 runtime 核心库，里面只保留：

- `Identity`
- `Invocation`
- `Messaging`
- `Routing`
- `Runtime`
- `Scheduling`
- `Host`

这层是“核心语义”。

### 6.2 第二层：生成与契约

把生成相关的东西单独拎出去：

- contracts
- generated references
- generated invokables
- serializers / manifests

这层是“编译期闭环”。

### 6.3 第三层：demo 和 playground

保留一个实验区，用来快速验证边界。

它可以像现在这个 `OrleansReplicaKernel` 一样，先手写壳，再逐步替换成生成产物。

这样做的好处是：

- 核心不会被 demo 牵着跑
- 生成逻辑不会污染 runtime
- 实验可以快，内核可以稳

---

## 7. 测试与验证策略

第一版的测试不用铺太大，但要分层。

### 7.1 单元验证

先测最小语义：

- `GrainId` 和 `GrainAddress`
- locator 和 router 的映射
- activation directory 的复用逻辑
- scheduler 的串行顺序

### 7.2 集成验证

再测一条完整链路：

- 一个 grain 调用能否穿过 runtime
- 两个 grain 是否复用同一套基础设施
- 第二次调用是否命中同一 activation

### 7.3 生成物验证

如果接 source generator，应该补两类验证：

- 生成产物快照测试
- 生成产物和运行时接口的一致性测试

### 7.4 演示验证

最后保留一个可执行 demo。

这不是玩具，而是每次改边界后最直接的烟雾测试。

---

## 8. 从 toy 到真正 runtime 的迁移步骤

现在的 `OrleansReplicaKernel` 已经证明了最小调用链能跑。

接下来应该按这个顺序往前推。

### 8.1 先抽接口

先把 toy 里的实现抽成正式接口，不要急着补能力。

重点是：

- `IGrainLocator`
- `IGrainRouter`
- `IActivationDirectory`
- `IInvocationRuntime`
- scheduler / host 的协作接口

### 8.2 再替换手写壳

把手写 reference 和 invokable 逐步换成生成产物。

这一步的目标不是完美 codegen，而是让“编译期生成”成为默认方向。

### 8.3 再补生命周期

当单节点主干稳定后，再加：

- activation 回收
- idle 失效
- 显式卸载
- 简单的健康检查

### 8.4 再补能力模块

最后才往上叠：

- state
- timer / reminder
- streaming
- transactions
- security
- diagnostics

顺序不能倒。

---

## 9. 这版计划真正想守住什么

这篇的收口点其实很简单。

我不是想把 Orleans 原样搬一遍。

我想先做出一版“足够干净的最小内核”，让后面每加一层能力，都知道自己是往哪里挂、为什么挂、挂完之后边界有没有被弄脏。

如果第一版能做到这件事，后面不管是补分布式、补代码生成、补能力模块，都会轻很多。

如果第一版做不稳，后面只会一直修旧账。
