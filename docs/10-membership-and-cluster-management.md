# Membership 与集群管理：IMembershipManager、MembershipTableManager、ClusterMembershipService 是怎么串起来的（第十篇）

这一篇专门讲 Orleans 的集群视图。

如果说前面几篇讲的是“一个调用怎么跑起来”，那这一篇讲的就是“这个集群现在还活着没有，谁算活的，谁该被踢出去，谁又该把这张图同步给别人”。

先把结论说清楚：

- `MembershipTableManager` 是真正改集群状态的地方。
- `ClusterMembershipService` 是给其他组件看的发布层，它把 membership table 的变化变成可订阅的 snapshot 流。
- `IMembershipManager` 是内部控制面，负责 suspect、kill、refresh、IAmAlive、process gossip 这些动作。
- `SiloStatusOracle` 是读模型，给放置、目录、连接维护这些地方提供“近似当前状态”。
- `MembershipAgent`、`ClusterHealthMonitor`、`RemoteSiloProber` 负责探活和故障推进。
- `SiloStatusListenerManager` 把状态变化往外广播。

如果要用一句人话概括：

> `MembershipTableManager` 管事实，`ClusterMembershipService` 管传播，`SiloStatusOracle` 管读取，`MembershipAgent` 管确认别人是不是还活着。

---

## 1. 先把整条链压成一张图

```text
Silo 启动
  -> DefaultSiloServices 注册 membership 相关服务
  -> MembershipTableManager
  -> MembershipGossiper
  -> RemoteSiloProber
  -> SiloStatusOracle
  -> ClusterHealthMonitor
  -> MembershipAgent
  -> SiloStatusListenerManager
  -> ClusterMembershipService

运行中
  -> MembershipTableManager 读取 membership table
  -> 维护本地 snapshot
  -> 定期 update IAmAlive
  -> gossip / probe / suspect / kill
  -> ClusterMembershipService 发布 ClusterMembershipSnapshot
  -> CachedGrainLocator / QueueBalancerBase / ClientDirectory 等订阅更新
  -> SiloStatusOracle 提供近似状态给 placement / directory / networking
```

这张图里最容易混淆的地方有三个。

第一，`MembershipTableManager` 和 `ClusterMembershipService` 都像“membership 服务”，但前者是写模型，后者是发布模型。

第二，`SiloStatusOracle` 不是 membership table 本身，它更像一个给其他组件用的状态镜子。

第三，Orleans 把探活、故障标记、表刷新、gossip、监听器广播拆成了好几个类，名字又都很像，读源码时特别容易绕。

---

## 2. 默认服务里，membership 是怎么被装进去的

关键文件：

- `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`
- `src/Orleans.Runtime/Hosting/SiloBuilder.cs`

`SiloBuilder` 一创建就会走 `DefaultSiloServices.AddDefaultServices(this)`。

在这套默认装配里，membership 相关的东西基本是一整组一起上的：

- `IMembershipManager -> MembershipTableManager`
- `IMembershipGossiper -> MembershipGossiper`
- `IRemoteSiloProber -> RemoteSiloProber`
- `ISiloStatusOracle -> SiloStatusOracle`
- `IClusterHealthMonitor -> ClusterHealthMonitor`
- `MembershipAgent`
- `SiloStatusListenerManager`
- `IClusterMembershipService -> ClusterMembershipService`
- `ClientDirectory`

也就是说，membership 不是一个单独的 service，而是一串彼此依赖的 runtime 组件。

这也解释了为什么 Orleans 的默认服务注册看起来总是特别厚。它不是补一个接口，而是把整条控制链都拉起来了。

---

## 3. `MembershipTableManager` 才是改状态的人

关键文件：

- `src/Orleans.Runtime/MembershipService/MembershipTableManager.cs`

`MembershipTableManager` 是 `IMembershipManager` 的核心实现。

它干的事情可以直接看成四类：

1. 读 membership table
2. 写 membership table
3. gossip / refresh / iAmAlive
4. suspect / kill / cleanup / status transition

### 3.1 它维护自己的 snapshot

它内部持有一个 `MembershipTableSnapshot`，并通过 `AsyncEnumerable<MembershipTableSnapshot>` 对外发布更新。

这个 snapshot 不是摆设，而是很多上层组件真正拿来做判断的基础。

### 3.2 它负责初始化和清理

启动时它会：

- 初始化 membership table
- 做首次 refresh
- 清理旧条目
- 检查是否存在节点迁移

这意味着 membership manager 不是“状态出来以后再管”，而是集群启动阶段就要参与进来。

### 3.3 它负责把本地状态写回表

它暴露的接口里有：

- `UpdateLocalStatus`
- `TryKillSilo`
- `TrySuspectSilo`
- `Refresh`
- `ProcessGossipSnapshot`
- `UpdateIAmAlive`

这些名字看着像一堆动作，实际上都在围绕一个事实：

集群状态最终要落到 membership table，其他机器再通过这张表同步自己的视图。

### 3.4 它把失败检测和表刷新揉在了一起

这个地方是 Orleans 一直不算干净的地方之一。

`MembershipTableManager` 同时做：

- table 读写
- gossip
- 心跳上报
- suspect / kill
- cleanup
- 本地状态推进

这几个概念其实可以拆得更开，但 Orleans 现在是绑在一起的。

---

## 4. `ClusterMembershipService` 负责把写模型变成可观察流

关键文件：

- `src/Orleans.Runtime/MembershipService/ClusterMembershipService.cs`
- `src/Orleans.Runtime/MembershipService/IClusterMembershipService.cs`

`ClusterMembershipService` 不是写表的人。

它做的是把 `IMembershipManager` 的 `MembershipUpdates` 转成一个稳定的 `ClusterMembershipSnapshot` 流，然后让其他组件订阅。

它的作用可以理解成：

- `MembershipTableManager` 给我一份最新事实
- 我把它整理成更适合业务消费的快照
- 我再把这个快照流广播出去

### 4.1 它有自己的版本控制

它维护一个 `ClusterMembershipSnapshot`，并通过版本号决定是否需要刷新。

这个层的意义是：

- 上游 table 可能更新得很频繁
- 下游组件不需要直接盯着表
- 它们只要消费 snapshot 更新就行

### 4.2 它在生命周期里启动监听

`ClusterMembershipService` 会在 `RuntimeInitialize` 阶段启动 membership update 的消费循环。

这样一来，其他组件只要订阅它的 `MembershipUpdates`，就能收到集群变化。

这条流后面会被很多地方用到。

比如：

- `CachedGrainLocator`
- `QueueBalancerBase`
- `ClientDirectory`
- 一些 placement / networking 组件

---

## 5. `SiloStatusOracle` 是给大家看的读模型

关键文件：

- `src/Orleans.Runtime/MembershipService/SiloStatusOracle.cs`
- `src/Orleans.Runtime/MembershipService/SiloStatusListenerManager.cs`

`SiloStatusOracle` 不负责写表，也不负责探活。

它更像一个状态查询器，给其他运行时组件提供“现在哪些 silo 是 active、dead、suspect”的近似视图。

这在 Orleans 里很重要，因为很多地方都不想直接依赖 membership table 本体。

典型消费方有：

- `PlacementService`
- `MessageCenter`
- `LocalGrainDirectory`
- `DeploymentLoadPublisher`
- `ClusterHealthMonitor`

### 5.1 它和 listener manager 是一对

`SiloStatusListenerManager` 负责收集对 silo 状态变化感兴趣的组件，然后在 status 变化时通知它们。

这层设计的思路很明显：

- `oracle` 提供状态读
- `listener manager` 负责状态广播

但这俩名字都挺像，第一次读代码很容易看懵。

---

## 6. 故障检测链路是怎么跑的

关键文件：

- `src/Orleans.Runtime/MembershipService/MembershipAgent.cs`
- `src/Orleans.Runtime/MembershipService/ClusterHealthMonitor.cs`
- `src/Orleans.Runtime/MembershipService/RemoteSiloProber.cs`
- `src/Orleans.Runtime/MembershipService/LocalSiloHealthMonitor.cs`

### 6.1 `MembershipAgent` 负责探活

它会按照 membership 视图去 probe 其他 silo。

如果探活失败，后续就可能进入 suspect 或 dead 的推进流程。

### 6.2 `ClusterHealthMonitor` 是默认的探活健康监控实现

它接收 `IMembershipManager` 的状态变化，并把健康状态和 membership 结合起来看。

这不是单纯的 ping/pong。

它还会把 cluster 状态变化向运行时其他部分传播。

### 6.3 `RemoteSiloProber` 是远端探测的执行器

`MembershipAgent` 或健康监控组件会通过它去 probe 远端 silo。

这层存在的意义是把“怎么探活”与“什么时候探活”拆开。

### 6.4 `TrySuspectSilo` / `TryKillSilo` 是最终的控制动作

这些动作最后还是会回到 `MembershipTableManager`，把状态写进 membership table。

所以故障检测的本质不是“谁喊一声不活了”，而是：

- 先观测
- 再确认
- 再更新 table
- 再广播给全局

---

## 7. 集群视图怎么影响别的系统

这部分很关键，因为 membership 不是孤立的。

### 7.1 `CachedGrainLocator` 会订阅 membership 更新

目录缓存需要知道哪些 silo 已经死了、哪些条目要清掉。

否则它会一直拿旧地址。

### 7.2 `QueueBalancerBase` 会跟着 membership 更新调整 active silo 集合

Streaming 里的 queue balance 就是典型例子。

`QueueBalancerBase` 会监听 `IClusterMembershipService.MembershipUpdates`，拿当前活着的 silo 集合去做 queue distribution 决策。

### 7.3 `ClientDirectory` 也盯着 membership

客户端目录需要知道本地连接的 client 和 cluster membership 的变化，才能维护 routing 信息。

### 7.4 `PlacementService` 和 `SiloStatusOracle` 也会依赖这份视图

放置、路由、连接维护、目录更新，最后都绕不开这张活跃节点视图。

所以 membership 不是集群边角料，而是整套 runtime 的地基。

---

## 8. 这块里最不够干净的地方

### 8.1 名字太像，角色太多

`MembershipTableManager`、`ClusterMembershipService`、`ClusterHealthMonitor`、`SiloStatusOracle`、`MembershipAgent`、`MembershipGossiper`、`MembershipTableCleanupAgent`。

这些名字看着都合理，但整体拼起来就很重。

### 8.2 写模型和读模型边界不够锋利

有些组件在写状态，有些组件在发 snapshot，有些组件在做状态镜像，有些组件在做监听广播。

理论上可以拆得更清楚，Orleans 现在是靠多个类一起兜住。

### 8.3 故障检测、table refresh、gossip 和生命周期绑得太紧

这几个动作其实是不同层次的事，但 Orleans 把它们集中到了 membership service 的核心链上。

这会让系统很强，也会让理解成本偏高。

### 8.4 读视图太依赖“近似”语义

`ISiloStatusOracle` 不是强一致事实，它更像给 runtime 其他部分的快速近似图。

这件事没问题，但它意味着很多逻辑默认都在吃一个“够快但不一定完全同步”的视图。

---

## 9. 如果你要自己复刻，我会怎么拆

### 9.1 Membership Store

只负责 membership table 的读写和版本推进。

### 9.2 Health Detection

只负责 probe、suspect、kill、IAmAlive 这一条线。

### 9.3 Cluster View Publisher

只负责把 table / health 的变化变成可订阅 snapshot。

### 9.4 Status Oracle

只负责给 placement、directory、networking 提供快速读视图。

这样拆以后，日志、状态迁移、探活、广播会清楚很多。

---

## 10. 推荐阅读顺序

建议按这个顺序读：

1. `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`
2. `src/Orleans.Runtime/MembershipService/IMembershipManager.cs`
3. `src/Orleans.Runtime/MembershipService/MembershipTableManager.cs`
4. `src/Orleans.Runtime/MembershipService/ClusterMembershipService.cs`
5. `src/Orleans.Runtime/MembershipService/SiloStatusOracle.cs`
6. `src/Orleans.Runtime/MembershipService/MembershipAgent.cs`
7. `src/Orleans.Runtime/MembershipService/ClusterHealthMonitor.cs`
8. `src/Orleans.Runtime/MembershipService/RemoteSiloProber.cs`
9. `src/Orleans.Runtime/MembershipService/SiloStatusListenerManager.cs`
10. `src/Orleans.Runtime/GrainDirectory/CachedGrainLocator.cs`
11. `src/Orleans.Streaming/QueueBalancer/QueueBalancerBase.cs`
12. `src/Orleans.Runtime/GrainDirectory/ClientDirectory.cs`

---

## 11. 这一篇的落点

把这一篇压成一句话，就是：

Orleans 的 membership 不是一张表，而是一整条状态传播链。

这条链把事实写入、探活、gossip、健康监控、快照广播、状态近似读取全连在一起，所以它很强，也很厚。

如果后面继续写，我建议下一篇接：

- `Client 侧连接、gateway 和 callback 链`

这样 membership、路由、连接、回调这几层就能完全接上了。

