# Orleans 源码总览（第一篇）

这份文档是给“准备完整复刻 Orleans”的人看的开篇导航。

目标不是把每个类逐行解释，而是先把这几个问题讲清楚：

1. Orleans 这套东西到底由哪几层组成。
2. 每个特性大概落在哪些包、哪些核心实现里。
3. 哪些是必须先复刻的内核，哪些是后挂的能力。
4. 哪些地方确实容易让人觉得“架构不够干净”。

我对这份仓库的第一判断是：

- Orleans 对外的编程模型是干净的，重点是 `grain + 强类型接口 + 透明远程调用 + 托管生命周期`。
- Orleans 对内的实现不算“极简”，而是一个把编译期代码生成、运行时调度、网络、目录、放置、存储、特性包、Provider 生态慢慢叠出来的大系统。
- 如果目标是“完整复刻”，最正确的做法不是一上来复制所有包，而是先搭出最小内核，再逐个接入提醒、流、事务、事件溯源、Dashboard、Provider。

---

## 1. 仓库一眼看懂

### 1.1 规模

我先做了一个粗盘点：

- `src`: 63 个 `csproj`，约 1814 个 `.cs` 文件
- `test`: 48 个 `csproj`，约 1048 个 `.cs` 文件
- `playground`: 11 个 `csproj`
- `samples`: 这里只有说明文档，示例代码已经迁到外部 `dotnet/samples`

这意味着 Orleans 不是一个“单内核 + 少量插件”的仓库，而是：

- 核心框架
- 编译期工具链
- 大量特性包
- 一整套 Provider 生态
- 完整测试基建

都在一个仓库里。

### 1.2 先记住的几个事实

- 根目录没有 `.sln`，仓库是按项目集合直接构建的。
- `global.json` 指向 `.NET SDK 10.0.201`。
- 根目录 `Directory.Build.props` 会给整个仓库统一开 `Nullable`、严格构建、文档生成、以及 Orleans 自己的编译时代码生成接入。
- `src/api` 不是业务代码，而是公开 API surface 的生成快照，适合拿来做“兼容性对照表”。
- `src/Orleans.Client`、`src/Orleans.Server`、`src/Orleans.Sdk` 基本都是聚合/Meta Package，不是主要实现所在地。

### 1.3 代码量最大的几个区域

从阅读优先级看，下面这些目录最值得先盯住：

| 目录 | 约 `.cs` 文件数 | 说明 |
| --- | ---: | --- |
| `src/Orleans.Runtime` | 266 | 运行时主战场：激活、目录、成员管理、消息、调度、放置 |
| `src/Orleans.Core` | 257 | 客户端/服务端共享运行时能力、Hosting、消息、公用基础设施 |
| `src/Orleans.Streaming` | 172 | 流系统本体，体量很大，别低估 |
| `src/Orleans.Serialization` | 159 | Orleans 自己的序列化系统 |
| `src/Azure` | 144 | Azure 生态下的各种 Provider |
| `src/Orleans.Core.Abstractions` | 126 | 对外编程模型、ID、Grain 基类、放置属性、版本属性等 |

如果你只看名字，很容易以为 `Core` 才是主角，但真正跑起来时，`Core + Runtime + Serialization + CodeGenerator` 才是最关键的组合。

---

## 2. 仓库分层

我会把 Orleans 仓库分成七层来理解。

### 2.1 编程模型层

主要目录：

- `src/Orleans.Core.Abstractions`
- `src/Orleans.Serialization.Abstractions`

这层定义的是“开发者看到的 Orleans 世界”：

- `Grain` / `Grain<TState>`
- `IGrain` / `IGrainFactory`
- `GrainId` / `GrainType` / `SiloAddress`
- `RequestContext`
- 放置、并发、版本、Provider 等特性属性
- 序列化标记，如 `[GenerateSerializer]`、`[Id]`

这一层很重要，因为你将来如果要“复刻 Orleans 风格”，真正决定用户体验的不是 `Catalog` 或 `MessageCenter`，而是这里的 API 形状。

### 2.2 编译期工具链层

主要目录：

- `src/Orleans.Sdk`
- `src/Orleans.CodeGenerator`
- `src/Orleans.Analyzers`

这层决定 Orleans 为什么能提供“强类型远程调用”而不是让用户手写 RPC。

它主要干三件事：

- Source Generator 生成代理、invokable、serializer、activator
- Analyzer 在编译期检查 Orleans 用法
- SDK 把这些工具以 Meta Package 的方式塞进用户项目

一句话概括：没有这层，Orleans 还是能做，但用户体验会从“框架”退回“库”。

### 2.3 共享运行时层

主要目录：

- `src/Orleans.Core`
- `src/Orleans.Serialization`

这层是客户端和 Silo 共享的底座，包含：

- Builder 和默认服务注册
- 公共消息模型
- 公共 Grain Reference 运行时
- 类型/接口元数据
- 序列化系统
- 客户端网关连接管理

### 2.4 服务端运行时层

主要目录：

- `src/Orleans.Runtime`

这是 Orleans 真正的“发动机舱”：

- Silo 生命周期
- Catalog/Activation
- Grain Directory
- Placement
- Membership
- Silo 间消息与连接
- 调度器
- 持久化 Facet

### 2.5 特性包层

主要目录：

- `src/Orleans.Reminders`
- `src/Orleans.Streaming`
- `src/Orleans.Transactions`
- `src/Orleans.EventSourcing`
- `src/Orleans.DurableJobs`
- `src/Orleans.Journaling`
- `src/Orleans.BroadcastChannel`
- `src/Dashboard/Orleans.Dashboard`
- `src/Orleans.Hosting.Kubernetes`
- `src/Orleans.Connections.Security`
- `src/Orleans.TestingHost`

这层不是“没有它 Orleans 就不能活”，而是“有了它 Orleans 才变成一个完整生态”。

这里有两个容易误判的点：

- `Orleans.Client` / `Orleans.Server` 更像“打包入口”，不是核心实现目录。
- Dashboard 也不是只有一个包，后端在 `src/Dashboard/Orleans.Dashboard`，前端页面资源在 `src/Dashboard/Orleans.Dashboard.App`。

### 2.6 Provider 生态层

主要目录：

- `src/Azure`
- `src/AWS`
- `src/AdoNet`
- `src/Redis`
- `src/Cassandra`
- `src/Serializers`
- `src/Orleans.Clustering.Consul`
- `src/Orleans.Clustering.ZooKeeper`

它们把 Orleans 的抽象落到具体基础设施上，比如：

- Cluster Membership / Clustering
- Grain Storage
- Grain Directory
- Reminders
- Streams
- Transactions storage
- Journaling storage

### 2.7 测试与演示层

主要目录：

- `test`
- `playground`
- `samples`

这里对你后续继续研究特别重要：

- `test` 是最靠谱的“行为定义”
- `playground` 是一些偏实验性/专题性的落地环境
- `samples` 现在主要是外部链接，不是本仓库主学习入口

---

## 3. Orleans 的主干运行链路

如果把整个系统压成一条主链，可以这么看：

```text
Grain 接口 / 状态类型 / 特性属性
    ↓
Orleans.Abstractions + Serialization.Abstractions
    ↓
CodeGenerator / Analyzer / SDK
    ↓
ClientBuilder / SiloBuilder
    ↓
DefaultClientServices / DefaultSiloServices
    ↓
GrainFactory / GrainReference / RuntimeClient
    ↓
MessageFactory / MessageSerializer / MessageCenter
    ↓
PlacementService + GrainLocator + Membership + Manifest
    ↓
Catalog + ActivationData + Scheduler + GrainActivator
    ↓
Grain 实例
    ↓
Persistence / Timers / Reminders / Streams / Transactions / ...
    ↓
Response -> RuntimeClient -> 调用方
```

这条链路里最核心的几个节点是：

1. 编译期生成的代理与序列化代码
2. `GrainFactory` 和 `GrainReference`
3. `RuntimeClient`
4. `MessageCenter`
5. `PlacementService` + `GrainLocator`
6. `Catalog`
7. `ActivationData`

只要这几个节点读透了，整个 Orleans 的骨架基本就站起来了。

---

## 4. 主干模块拆解

## 4.1 对外编程模型

关键文件：

- `src/Orleans.Core.Abstractions/Core/Grain.cs`
- `src/Orleans.Core.Abstractions/Core/IGrainFactory.cs`
- `src/Orleans.Core.Abstractions/Runtime/GrainReference.cs`
- `src/Orleans.Core.Abstractions/Runtime/RequestContext.cs`
- `src/Orleans.Core.Abstractions/IDs/*`
- `src/Orleans.Serialization.Abstractions/Annotations.cs`

这一层回答的是：开发者怎么写 Orleans 代码。

几个关键点：

- Grain 身份是稳定的，激活不是稳定的。
- 调用方拿到的是 `GrainReference`，不是对象实例。
- `IGrainFactory` 负责把“接口 + Key”变成 Grain 引用。
- 持久化状态、放置策略、版本兼容、并发策略很多都是通过 Attribute 声明的。

这里有一个很值得记住的设计味道：

- 对用户暴露的是强类型接口和普通方法调用
- 对框架内部则是 `GrainId + InterfaceType + Message + Invokable`

也就是说，外面是“对象模型”，里面是“消息模型”。

## 4.2 编译期代码生成

关键文件：

- `src/Orleans.CodeGenerator/OrleansSourceGenerator.cs`
- `src/Orleans.CodeGenerator/ProxyGenerator.cs`
- `src/Orleans.CodeGenerator/InvokableGenerator.cs`
- `src/Orleans.CodeGenerator/SerializerGenerator.cs`
- `src/Orleans.Sdk/Orleans.Sdk.csproj`

这部分是 Orleans 很难绕过去的核心。

它生成的东西主要包括：

- Grain 接口代理
- 方法调用的 invokable 对象
- 序列化器/拷贝器
- 一部分元数据与激活辅助代码

如果你以后真要“复刻一版”，我建议把这块视为 P0，而不是“以后再补”。

原因很简单：

- 不做代码生成，就很难保住 Orleans 现在的类型安全和易用性
- 很多运行时路径默认已经建立在“有生成代码”的前提上

## 4.3 Hosting 与默认服务装配

关键文件：

- `src/Orleans.Runtime/Hosting/OrleansSiloGenericHostExtensions.cs`
- `src/Orleans.Core/Hosting/OrleansClientGenericHostExtensions.cs`
- `src/Orleans.Runtime/Hosting/SiloBuilder.cs`
- `src/Orleans.Core/Core/ClientBuilder.cs`
- `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`
- `src/Orleans.Core/Core/DefaultClientServices.cs`

这一层做的事非常朴素：

- 把 Orleans 接进 `Generic Host`
- 把客户端和 Silo 的默认服务全部塞进 DI
- 把 `Orleans:*` 配置节映射到运行时配置
- 自动发现和安装 Provider

这也是 Orleans 内部一个“既强大又容易变乱”的点。

原因是：

- 几乎所有东西最后都得在这里注册
- 一堆默认实现、选项、生命周期参与者、Provider 都在这里汇合

从阅读角度看，`DefaultSiloServices.cs` 基本就是 Orleans 运行时装配总表。

## 4.4 类型元数据、Manifest 与版本

关键文件：

- `src/Orleans.Runtime/Manifest/SiloManifestProvider.cs`
- `src/Orleans.Runtime/Manifest/ClusterManifestProvider.cs`
- `src/Orleans.Core/Manifest/ClientManifestProvider.cs`
- `src/Orleans.Core/Manifest/GrainTypeResolver.cs`
- `src/Orleans.Core/Manifest/GrainInterfaceTypeResolver.cs`

这套东西回答的是两个问题：

1. 这个接口/类在 Orleans 世界里到底叫什么。
2. 集群里哪些 Silo 支持哪些 Grain 类型与接口版本。

它既服务于：

- 代码生成后的类型绑定
- 放置与路由
- 版本兼容与异构集群

也就是说，Manifest 不是“文档元数据”，而是运行时决策输入。

## 4.5 客户端、消息与网关

关键文件：

- `src/Orleans.Core/Runtime/OutsideRuntimeClient.cs`
- `src/Orleans.Core/Messaging/ClientMessageCenter.cs`
- `src/Orleans.Core/Messaging/GatewayManager.cs`
- `src/Orleans.Runtime/Core/InsideRuntimeClient.cs`
- `src/Orleans.Runtime/Messaging/MessageCenter.cs`
- `src/Orleans.Core/Messaging/Message.cs`
- `src/Orleans.Core/Messaging/MessageFactory.cs`
- `src/Orleans.Core/Messaging/MessageSerializer.cs`

Orleans 的调用不是“直接找对象”，而是：

- 先通过 `GrainReference` 生成请求
- 再转成 `Message`
- 由 RuntimeClient 送到 MessageCenter
- 再做寻址、转发、投递

客户端和 Silo 侧各有一套 RuntimeClient：

- `OutsideRuntimeClient`: 外部客户端用
- `InsideRuntimeClient`: Silo 内部用

客户端不是直接连任意 Silo，而是优先通过 Gateway 接入。

这里的关键点包括：

- 回调与超时管理
- 请求/响应关联
- Gateway 选择与刷新
- 本地消息和远程消息的统一处理
- 失效地址缓存的更新与转发

## 4.6 目录、放置、成员管理

关键文件：

- `src/Orleans.Runtime/GrainDirectory/GrainLocator.cs`
- `src/Orleans.Runtime/GrainDirectory/LocalGrainDirectory.cs`
- `src/Orleans.Runtime/GrainDirectory/DistributedGrainDirectory.cs`
- `src/Orleans.Runtime/Placement/PlacementService.cs`
- `src/Orleans.Runtime/Placement/*Director.cs`
- `src/Orleans.Runtime/MembershipService/*`
- `src/Orleans.Runtime/ConsistentRing/*`

这部分决定“请求该发到哪台 Silo”。

它内部其实是三套机制一起工作：

- Grain Directory：查 Grain 现在在哪
- Placement：如果 Grain 还没激活，决定激活到哪
- Membership：知道集群里现在谁活着、谁死了、谁可用

而且它们又跟 Manifest/Versioning 挂着：

- 不是所有 Silo 都一定支持同一套 Grain 类型和接口版本

这就是为什么 Orleans 的“路由”不是一个普通哈希表，而是一整套组合策略。

## 4.7 激活、生命周期与调度

关键文件：

- `src/Orleans.Runtime/Catalog/Catalog.cs`
- `src/Orleans.Runtime/Catalog/ActivationData.cs`
- `src/Orleans.Runtime/Activation/DefaultGrainActivator.cs`
- `src/Orleans.Runtime/Facet/GrainConstructorArgumentFactory.cs`
- `src/Orleans.Runtime/Scheduler/ActivationTaskScheduler.cs`
- `src/Orleans.Runtime/Scheduler/WorkItemGroup.cs`

这部分是 Orleans 最核心、也最容易“长胖”的地方。

大概流程是：

1. `Catalog` 查本地有没有现成激活
2. 没有就创建 `ActivationData`
3. `ActivationData` 挂上自己的调度器、作用域、生命周期、组件表
4. `DefaultGrainActivator` 真正构造 Grain 实例
5. 激活开始收消息并执行

这里要特别注意两个点：

### A. `ActivationData` 是运行时大枢纽

它同时承担了很多职责：

- Grain 上下文
- 消息等待队列与执行队列
- Grain 扩展绑定
- 定时器注册
- 生命周期/停机状态
- 取消请求
- 迁移/rehydration

这也是我目前最认可的“架构不够干净”的证据之一。

它不是单点小对象，而是一个运行时聚合体。

### B. Grain 构造走的是“DI + Facet Attribute 映射”

例如持久化状态不是硬编码进 `Grain<TState>` 的唯一入口。

Orleans 还支持这种构造参数注入模式：

- 参数上打 `[PersistentState(...)]`
- `GrainConstructorArgumentFactory` 找到参数上的 Facet 元数据
- `IAttributeToFactoryMapper<T>` 把 Attribute 映射成工厂
- 运行时在激活时生成对应依赖

这一点对复刻很关键，因为它说明 Orleans 的 Grain 构造不是单纯 `ActivatorUtilities`，而是带了一层“Attribute 驱动的参数工厂”。

## 4.8 状态与持久化

关键文件：

- `src/Orleans.Runtime/Storage/StateStorageBridge.cs`
- `src/Orleans.Runtime/Facet/Persistent/PersistentStateFactory.cs`
- `src/Orleans.Runtime/Facet/Persistent/PersistentStateAttributeMapper.cs`
- `src/Orleans.Core/Providers/IGrainStorage.cs`
- `src/Orleans.Runtime/Hosting/StorageProviderHostExtensions.cs`

持久化这块内部结构其实挺清晰：

- `IGrainStorage` 是底层 Provider 抽象
- `StateStorageBridge<T>` 是运行时桥接层
- `IPersistentState<T>` / `Grain<TState>` 是给 Grain 用的上层接口

Bridge 这一层很重要，它不只是简单代理：

- 负责读写包装
- 负责错误包装
- 负责 metrics 和 tracing
- 负责迁移时的状态携带

所以 Orleans 的持久化并不是“Grain 直接调 Provider”，中间专门垫了一层运行时桥。

## 4.9 序列化

关键文件：

- `src/Orleans.Serialization/Hosting/ServiceCollectionExtensions.cs`
- `src/Orleans.Serialization/Serializer.cs`
- `src/Orleans.Serialization/Serializers/CodecProvider.cs`
- `src/Orleans.Serialization/Codecs/*`
- `src/Orleans.Serialization/Cloning/*`
- `src/Orleans.Serialization/WireProtocol/*`

这是另一个不能低估的核心。

Orleans 的序列化系统不是“给 RPC 用一下”这么简单，它同时服务于：

- 网络消息
- Grain 调用参数
- 状态存储
- Deep Copy
- 生成代码与运行时 Codec 组合

此外还挂了一批适配包：

- `Orleans.Serialization.SystemTextJson`
- `Orleans.Serialization.NewtonsoftJson`
- `Orleans.Serialization.MessagePack`
- `Orleans.Serialization.MemoryPack`
- `Orleans.Serialization.Protobuf`
- `Orleans.Serialization.FSharp`

如果你以后打算做“兼容 Orleans API 的复刻版”，序列化格式和属性模型最好尽早定下来，不要拖到最后。

---

## 5. 功能地图

下面这部分不是“所有类清单”，而是“功能 -> 包 -> 实现入口”的地图。

## 5.1 核心内建能力

| 能力 | 对外入口 | 主要实现位置 |
| --- | --- | --- |
| Grain 编程模型 | `Grain`、`IGrainFactory`、各种 `IGrainWith*Key` | `src/Orleans.Core.Abstractions` |
| Grain 引用与调用 | `GetGrain`、Grain proxy | `src/Orleans.Core.Abstractions`、`src/Orleans.CodeGenerator`、`src/Orleans.Core/Runtime` |
| Host 集成 | `UseOrleans`、`UseOrleansClient` | `src/Orleans.Runtime/Hosting`、`src/Orleans.Core/Hosting` |
| 客户端连接 | `ClientBuilder`、Gateway | `src/Orleans.Core/Core`、`src/Orleans.Core/Messaging` |
| Silo 运行时 | `Silo`、默认服务装配 | `src/Orleans.Runtime` |
| 激活与生命周期 | `Catalog`、`ActivationData`、`GrainLifecycle` | `src/Orleans.Runtime/Catalog`、`src/Orleans.Runtime/Scheduler` |
| Directory / 路由 | Grain 定位、缓存、失效更新 | `src/Orleans.Runtime/GrainDirectory` |
| 放置策略 | Placement Attribute + Director | `src/Orleans.Core.Abstractions/Placement`、`src/Orleans.Runtime/Placement` |
| 集群成员管理 | Membership / probe / gossip | `src/Orleans.Runtime/MembershipService` |
| 类型与版本元数据 | Manifest / version selector | `src/Orleans.Core/Manifest`、`src/Orleans.Runtime/Manifest`、`src/Orleans.Runtime/Versions` |
| 序列化 | `[GenerateSerializer]`、Codec、Deep Copy | `src/Orleans.Serialization*` |
| 持久化状态 | `IPersistentState<T>`、`Grain<TState>` | `src/Orleans.Runtime/Facet/Persistent`、`src/Orleans.Runtime/Storage` |
| 定时器 | Grain timer | `src/Orleans.Core.Abstractions/Timers`、`src/Orleans.Runtime/Timers` |
| Grain Call Filter | 入站/出站拦截 | `src/Orleans.Core.Abstractions/Core`、`src/Orleans.Runtime/Hosting/GrainCallFilterExtensions.cs` |
| Request Context | 链路元数据透传 | `src/Orleans.Core.Abstractions/Runtime/RequestContext.cs` |

## 5.2 独立特性包

| 特性 | 入口包 | 关键实现 |
| --- | --- | --- |
| Reminders | `src/Orleans.Reminders` | `LocalReminderService`、`ReminderRegistry`、`IReminderTable` |
| Streams | `src/Orleans.Streaming` | `PersistentStreamProvider`、`PersistentStreamPullingAgent`、`PubSubRendezvousGrain`、`MemoryStreams` |
| Transactions | `src/Orleans.Transactions` | `TransactionAgent`、`TransactionManager`、`TransactionalState` |
| Broadcast Channel | `src/Orleans.BroadcastChannel` | `BroadcastChannelProvider`、`BroadcastChannelWriter`、`ImplicitChannelSubscriberTable` |
| Durable Jobs | `src/Orleans.DurableJobs` | `LocalDurableJobManager`、`ShardExecutor`、`JobShardManager` |
| Event Sourcing | `src/Orleans.EventSourcing` | `JournaledGrain`、`LogConsistentGrain`、`LogViewAdaptor` |
| Journaling / State Machine | `src/Orleans.Journaling` | `StateMachineManager`、`DurableState`、`IStateMachineStorage` |
| Dashboard | `src/Dashboard/Orleans.Dashboard` | `DashboardHost`、`SiloGrainService`、`GrainProfilerFilter`，前端资源在 `src/Dashboard/Orleans.Dashboard.App` |
| TLS 安全连接 | `src/Orleans.Connections.Security` | `TlsClientConnectionMiddleware`、`TlsServerConnectionMiddleware` |
| Kubernetes Hosting | `src/Orleans.Hosting.Kubernetes` | `KubernetesClusterAgent` |
| Testing Host | `src/Orleans.TestingHost` | `TestCluster`、`InProcTestCluster`、`StandaloneSiloHost` |

## 5.3 Provider 生态

Orleans 的 Provider 接入方式很统一：

- 包里用 `[RegisterProvider(...)]` 做注册声明
- `DefaultSiloServices` / `DefaultClientServices` 扫描引用程序集
- 从 `Orleans:*` 配置节里读 `ProviderType`
- 交给 `IProviderBuilder<TBuilder>` 装配

对应实现主要分布在这些目录：

| 类别 | 目录 |
| --- | --- |
| Azure Provider | `src/Azure/*` |
| AWS Provider | `src/AWS/*` |
| ADO.NET Provider | `src/AdoNet/*` |
| Redis Provider | `src/Redis/*` |
| Cassandra Clustering | `src/Cassandra/*` |
| Consul / ZooKeeper Clustering | `src/Orleans.Clustering.Consul`、`src/Orleans.Clustering.ZooKeeper` |
| 第三方 Serializer | `src/Serializers/*` |

你如果要复刻，建议一开始只保留：

- 内存存储
- 本地开发集群
- 一个最简单的 Clustering Provider

先别把 Azure / Redis / ADO.NET 一起搬进去，不然第一阶段会被适配层拖死。

---

## 6. 推荐的第一轮阅读顺序

如果你准备继续往下深挖，我建议按这个顺序看，而不是在 `src` 里随机点文件。

1. `README.md`
2. `Directory.Build.props`
3. `src/Orleans.Core.Abstractions/Core/Grain.cs`
4. `src/Orleans.Core.Abstractions/Core/IGrainFactory.cs`
5. `src/Orleans.Serialization.Abstractions/Annotations.cs`
6. `src/Orleans.CodeGenerator/OrleansSourceGenerator.cs`
7. `src/Orleans.Core/Core/DefaultClientServices.cs`
8. `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`
9. `src/Orleans.Core/Runtime/OutsideRuntimeClient.cs`
10. `src/Orleans.Runtime/Core/InsideRuntimeClient.cs`
11. `src/Orleans.Runtime/Messaging/MessageCenter.cs`
12. `src/Orleans.Runtime/Placement/PlacementService.cs`
13. `src/Orleans.Runtime/GrainDirectory/GrainLocator.cs`
14. `src/Orleans.Runtime/Catalog/Catalog.cs`
15. `src/Orleans.Runtime/Catalog/ActivationData.cs`
16. `src/Orleans.Runtime/Storage/StateStorageBridge.cs`
17. `src/Orleans.Runtime/MembershipService/ClusterMembershipService.cs`
18. `src/Orleans.Streaming/Hosting/SiloBuilderStreamingExtensions.cs`
19. `src/Orleans.Transactions/Hosting/SiloBuilderExtensions.cs`
20. `src/Orleans.TestingHost/TestCluster.cs`

这 20 个文件看完，脑子里会先长出一个“Orleans 主干模型”。

---

## 7. 如果要完整复刻，我建议这样拆

这里说的是“工程顺序”，不是“概念顺序”。

## 7.1 P0：先做能跑起来的最小内核

先做这些：

- Grain ID / Grain Type / Grain Reference
- 基础 Attribute 和对外接口
- 代码生成最小闭环
- 序列化最小闭环
- Client / Silo Builder
- RuntimeClient + Message + MessageCenter
- Local Catalog + Activation + 单线程调度
- 最简单 Grain Directory
- 最简单 Placement
- In-memory 持久化

做到这一步，你应该能跑通：

- `client.GetGrain<T>(key)`
- 远程调用
- 本地激活
- 状态读写

## 7.2 P1：再补集群能力

- Membership
- Gateway / Client 连接管理
- Manifest / Versioning
- 分布式 Directory
- 更完整的 Placement

## 7.3 P2：再补 Orleans 的“像样特性”

- Reminders
- Streams
- Broadcast Channel
- Dashboard
- Testing Host

## 7.4 P3：最后再碰最重的高级能力

- Transactions
- Event Sourcing
- Journaling
- Durable Jobs
- Kubernetes Hosting
- TLS 连接
- 各种外部 Provider

如果反过来做，很容易陷入：

- 先做了很多扩展点
- 结果主内核还没稳定
- 最后发现很多接口形状都得重来

---

## 8. 目前能看到的几个“架构不够干净”的点

这里只说我已经在代码里看到证据的地方，不凭感觉乱喷。

### 8.1 默认服务装配过于集中

`DefaultSiloServices.cs` 和 `DefaultClientServices.cs` 像两张巨大的总装配表。

问题不是“写得差”，而是：

- 所有能力最后都要回到这里汇合
- 运行时、网络、目录、版本、持久化、序列化、可观测性、Provider 装配都挤在一起
- 后续新增能力时，天然会继续往这里堆

这类文件通常会越演进越像“隐式架构图”，不太像“清晰分层后的模块入口”。

### 8.2 `ActivationData` 职责太重

这个类同时实现了太多接口，也承担了太多状态：

- Grain Context
- 调度
- 消息排队与执行
- 扩展绑定
- 计时器
- 取消
- 迁移
- 生命周期状态

这类对象一旦成为运行时中枢，后面很多能力都会往它身上挂，复刻时最好主动拆小。

### 8.3 Client 和 Silo 的默认配置逻辑有明显镜像复制

`DefaultClientServices.ApplyConfiguration(...)` 和 `DefaultSiloServices.ApplyConfiguration(...)` 逻辑形状非常像：

- 扫 Provider
- 读 `Orleans:*` 配置
- 按节安装 Clustering / Streaming / Reminders / Storage 等能力

这说明 Orleans 现在的“Builder 配置系统”是强可用的，但复用边界没完全抽干净。

### 8.4 Provider 自动发现很方便，但有点隐式

Provider 是靠：

- `[RegisterProvider]`
- 程序集扫描
- 配置里的 `ProviderType`

串起来的。

好处是接入简单，坏处是：

- 装配逻辑不够显式
- 新人读代码时不容易一下子看清“这个 Provider 是怎么被发现的”

### 8.5 `Grain` 基类仍然带一点运行时上下文/服务定位器味道

例如：

- `RuntimeContext.Current`
- `GrainContext`
- `ServiceProvider`

这些东西对框架实现来说很实用，但从纯净架构角度看，会让“业务 Grain”与“运行时上下文”有比较紧的耦合。

我的结论是：

- Orleans 的对外模型很干净
- Orleans 的内部实现更像“高实用性工程系统”，不是“教科书式分层样板”

这也正是它值得复刻的地方：思路好，但实现可以再重新整理一次。

---

## 9. 后续文档建议

这只是第一篇。继续往下拆，我建议后面按子系统出文档。

推荐顺序：

1. 编译期链路：Attribute、Source Generator、Proxy、Invokable、Serializer 是怎么串起来的
2. 一次 Grain 调用从 `GetGrain` 到返回结果的完整链路
3. 激活系统：`Catalog + ActivationData + Scheduler + GrainActivator`
4. `ActivationData` 为什么会成为 Orleans 复杂度中心
5. Directory / Placement / Membership 三件套
6. 持久化系统：`PersistentState`、`StateStorageBridge`、Provider 接口
7. Streams 的完整结构：PubSub、Queue Adapter、Pulling Agent、Balancer
8. Transactions / Event Sourcing / Journaling 的设计关系
9. Provider 生态：Clustering、Storage、Reminders、Streaming 分别怎么接
10. TestingHost、Dashboard、Playground 作为“学习资料”应该怎么用

---

## 10. 这份开篇文档的结论

如果只用一句话总结 Orleans 源码：

> 它的外层是一个很顺手的 Virtual Actor 框架，它的内层是一台由代码生成、消息分发、目录寻址、激活调度、集群成员管理和大量插件点共同驱动的分布式运行时机器。

如果只用一句话总结“怎么复刻”：

> 不要按包名平铺复制，要先抓住 `Abstractions -> CodeGen -> Serialization -> RuntimeClient/MessageCenter -> Placement/Directory -> Catalog/Activation` 这条主骨架，再把 Reminders、Streams、Transactions 这些能力一个个挂回去。
