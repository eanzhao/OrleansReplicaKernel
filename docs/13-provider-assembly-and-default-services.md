# Provider 装配与默认服务注册：DefaultSiloServices、DefaultClientServices、builder extension 是怎么把 Orleans 组起来的（第十三篇）

这一篇专门讲 Orleans 最像“总装配表”的那一层。

前面几篇已经把调用、代码生成、序列化、调度、目录、状态、Timer/Reminder 这些主链拆开了。到了这一篇，视角往上抬一点，去看 Orleans 是怎么把这些东西真正装进容器里的。

先把结论说清楚：

- `DefaultSiloServices` 和 `DefaultClientServices` 不是普通的 helper，它们就是 Orleans 的默认装配入口。
- `SiloBuilder` / `ClientBuilder` 一创建，就会先跑这两套默认装配。
- `AddSerializer(...)` 负责把编译期生成出来的 `ApplicationPart` / `TypeManifestProvider` 吃进运行时。
- `RegisterProviderAttribute` 负责把 provider 的名字、种类、目标端暴露出来，供默认配置阶段反射发现。
- `IProviderBuilder<TBuilder>` 负责把“配置文件里的一个 provider 声明”翻译成具体的 `builder.Add...` 调用。
- Orleans 之所以容易变成总装配式架构，不是因为它不会拆，而是因为它把太多“默认行为”集中在 builder 构建阶段了。

如果你只记一句话，就是这个：

> Orleans 不是先有运行时，再往里挂功能；它更像是先有一张装配表，再把运行时拼出来。

---

## 1. 先把整条装配链压成一张图

```text
new SiloBuilder / new ClientBuilder
  -> DefaultSiloServices.AddDefaultServices / DefaultClientServices.AddDefaultServices
  -> 注册基础服务、运行时服务、序列化、目录、放置、membership、网络、provider runtime
  -> AddSerializer()
       -> ReferencedAssemblyProvider.GetRelevantAssemblies()
       -> 读取 ApplicationPartAttribute
       -> 读取 TypeManifestProviderAttribute
       -> 注册 TypeManifestOptions / TypeConverter / CodecProvider
  -> ApplyConfiguration(...)
       -> 读取 Orleans 配置树
       -> 用 RegisterProviderAttribute 找到 provider builder
       -> 根据 section kind / name / ProviderType 调用 IProviderBuilder.Configure(...)
       -> provider builder 再去调用具体的 builder.Add... / services.Add...
  -> 默认服务和 provider 服务一起进容器
  -> Orleans runtime 启动
```

这张图里最容易看错的点有两个。

第一，`DefaultSiloServices` / `DefaultClientServices` 不只是“补一些默认依赖”。它们把 Orleans 的大部分核心模块都预注册了。

第二，provider 的装配不是完全靠配置类手写绑定，而是有一层反射发现。你在配置里写一个名字，最终会被映射到某个 assembly 上的 `RegisterProviderAttribute` 和 `IProviderBuilder<TBuilder>` 实现。

---

## 2. builder 一创建，默认服务就先进来了

关键文件：

- `src/Orleans.Runtime/Hosting/SiloBuilder.cs`
- `src/Orleans.Core/Core/ClientBuilder.cs`
- `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`
- `src/Orleans.Core/Core/DefaultClientServices.cs`

### 2.1 `SiloBuilder` / `ClientBuilder` 都是“先装默认值，再谈用户配置”

这两类 builder 的构造函数都做了同一件事：

- `SiloBuilder(...)` -> `DefaultSiloServices.AddDefaultServices(this)`
- `ClientBuilder(...)` -> `DefaultClientServices.AddDefaultServices(this)`

也就是说，Orleans 不是等用户显式调用某个 `AddXxx()` 再开始装配。

它从 builder 创建那一刻就已经把默认运行时骨架塞进来了。

这件事的后果很直接：

- Orleans 的“什么是默认能力”不是一个轻量层
- 你即使什么都不配，也已经拿到一整套较完整的运行时
- 用户后续做的更多是覆盖、补丁和选择，而不是从零组装

### 2.2 默认服务不是一两个，而是一大串

`DefaultSiloServices.AddDefaultServices(...)` 里面的内容非常长，基本覆盖了：

- logging / options / metrics
- lifecycle
- statistics
- grain runtime / grain factory / cancellation
- directory / placement / activation
- membership / gossip / health
- messaging / network / connection
- serializer / copier / activator / type metadata
- timers / reminder / persistent state
- versioning / compatibility

`DefaultClientServices.AddDefaultServices(...)` 也类似，只是它偏向：

- client lifecycle
- 外部连接和 gateway
- 外部调用回调
- client 侧 grain reference / runtime client
- client 侧 provider runtime
- client 侧 type metadata

这说明 Orleans 的默认装配不是“公共基础设施 + 少量角色差异”，而是两张很大的总表。

### 2.3 它们还有一个防重复开关

两个方法都用一个 marker `ServiceDescriptor` 来防止重复添加。

这类写法很直白，也很 Orleans：

- 先给 service collection 塞一个内部标记
- 后面再进来时先检查有没有这个标记
- 有的话直接退出

它比“做一个专门的装配上下文对象”更粗一点，但实现上很简单。

---

## 3. DefaultSiloServices 和 DefaultClientServices 的分工其实很不对称

关键文件：

- `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`
- `src/Orleans.Core/Core/DefaultClientServices.cs`

### 3.1 Silo 侧更像完整运行时

Silo 侧默认服务几乎把整个集群都拉起来了：

- `Catalog`
- `PlacementService`
- `LocalGrainDirectory` / `GrainLocator`
- `MessageCenter`
- `MembershipTableManager`
- `ClusterMembershipService`
- `ActivationCollector`
- `TimerManagerImpl`
- `GrainCallCancellationManager`
- `PersistentStateFactory`
- `StateStorageBridgeSharedMap`

这意味着 Silo 的默认装配不只是“服务端 API 层”，而是完整 runtime。

### 3.2 Client 侧更像一个精简版 runtime

Client 侧默认服务重点放在：

- `OutsideRuntimeClient`
- `ClientMessageCenter`
- `GatewayManager`
- `ClusterClient`
- `GrainFactory`
- `GrainReferenceActivator`
- `Serializer`
- `ConnectionManager`
- `ClientProviderRuntime`

它没有 Silo 那么厚，但也绝对不是轻量 SDK。

一个 Orleans client 不是“只会发 HTTP 请求的薄壳”，而是一个带自己的引用系统、序列化系统、连接管理和回调链的 runtime。

### 3.3 两边其实共享了很多底层块

这也是 Orleans 容易让人觉得“装配太大”的原因之一。

虽然 Silo 和 Client 分工不同，但它们都要装：

- `AddSerializer()`
- `GrainReferenceActivator`
- `GrainFactory`
- `IGrainReferenceRuntime`
- `CodecProvider`
- `TypeConverter`
- `MessageFactory`
- `GrainTypeResolver`

也就是说，调用端和宿主端并没有完全分裂成两个世界，而是共享了很大一块基础设施。

---

## 4. `AddSerializer(...)` 是第二层总装配入口

关键文件：

- `src/Orleans.Serialization/Hosting/ServiceCollectionExtensions.cs`
- `src/Orleans.Serialization/Hosting/SerializerBuilderExtensions.cs`
- `src/Orleans.Serialization/Hosting/ReferencedAssemblyProvider.cs`
- `src/Orleans.Serialization/Configuration/TypeManifestProviderAttribute.cs`

### 4.1 `AddSerializer(...)` 不是只加一个 serializer

它会先扫描相关程序集，再把一整套序列化基础设施装进来：

- `TypeResolver`
- `TypeConverter`
- `CodecProvider`
- `WellKnownTypeCollection`
- `TypeCodec`
- `SerializerSessionPool`
- `CopyContextPool`
- `ObjectSerializer`
- `Serializer`
- `DeepCopier`

它还会把 `IFieldCodec<>`、`IBaseCodec<>`、`IValueSerializer<>`、`IActivator<>` 这些服务做成 holder，按需懒加载。

所以这里看起来是“加一个 serializer”，实际上是“把整个类型系统和 codec 系统挂上去”。

### 4.2 `ReferencedAssemblyProvider` 负责把相关程序集找出来

`AddSerializer(...)` 并不是只看当前项目。

它会通过 `ReferencedAssemblyProvider.GetRelevantAssemblies()` 去找带 `ApplicationPartAttribute` 的相关程序集，再逐个调用 `builder.AddAssembly(asm)`。

这一步的意义很大：

- 编译期生成的 manifest 不用手工一个个注册
- 运行时只要 assembly 上带了 `ApplicationPartAttribute`，就能被串进来
- Orleans 的“代码生成成果”和“运行时服务表”终于接上了

### 4.3 `ApplicationPartAttribute` 解决的是“这个程序集要不要算进来”

这个属性本身是 assembly 级别的。

它不是 provider，也不是服务。

它的作用很朴素：

- 这份程序集是 Orleans 应该关心的
- 这份程序集里有需要参与生成或装配的类型

`CodeGenerator` 会给相关程序集补上这个 attribute，运行时又靠它把程序集重新找回来。

这就是 Orleans 第三篇和第四篇之间真正的桥。

### 4.4 `TypeManifestProviderAttribute` 解决的是“去哪个类里拿 manifest”

`ApplicationPartAttribute` 只是告诉你“这个 assembly 要参与”。

`TypeManifestProviderAttribute` 才告诉你：

- 这个 assembly 的类型清单由谁提供
- 哪个 provider 类会把 `Serializers`、`Copiers`、`Activators`、`InterfaceProxies` 这些东西填进 `TypeManifestOptions`

换句话说：

- `ApplicationPartAttribute` 负责程序集发现
- `TypeManifestProviderAttribute` 负责 manifest 提供者发现

这俩经常一起出现，但不是一回事。

---

## 5. provider 的装配是怎么从配置文件落到具体代码的

关键文件：

- `src/Orleans.Serialization.Abstractions/Annotations.cs`
- `src/Orleans.Core.Abstractions/Providers/IProviderBuilder.cs`
- `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`
- `src/Orleans.Core/Core/DefaultClientServices.cs`
- `src/Orleans.Persistence.Memory/Hosting/MemoryGrainStorageProviderBuilder.cs`
- `src/Orleans.Clustering.Consul/ConsulClusteringProviderBuilder.cs`
- `src/Orleans.Streaming/MemoryStreams/MemoryStreamProviderBuilder.cs`
- `src/Orleans.Reminders/Hosting/MemoryReminderTableBuilder.cs`

### 5.1 `RegisterProviderAttribute` 是 provider 发现的钥匙

这个 attribute 是 assembly 级别的，长这样：

- provider `Name`
- provider `Kind`
- provider `Target`
- provider `Type`

比如你会看到类似：

- `RegisterProvider("Memory", "GrainStorage", "Silo", typeof(MemoryGrainStorageProviderBuilder))`
- `RegisterProvider("Consul", "Clustering", "Client", typeof(ConsulClusteringProviderBuilder))`
- `RegisterProvider("Memory", "Streaming", "Silo", typeof(MemoryStreamProviderBuilder))`

这就意味着 Orleans 不是靠约定式类名去猜 provider，而是靠 attribute 明牌。

### 5.2 `GetRegisteredProviders()` 会把它们扫成一个字典

`DefaultSiloServices.ApplyConfiguration(...)` 和 `DefaultClientServices.ApplyConfiguration(...)` 都会做同一件事：

1. 调 `ReferencedAssemblyProvider.GetRelevantAssemblies()`
2. 扫每个 assembly 上的 `RegisterProviderAttribute`
3. 按 `(kind, name)` 存成字典
4. 只保留目标端匹配的 provider

Silo 只收 `Target == "Silo"` 的 provider。

Client 只收 `Target == "Client"` 的 provider。

这也是为什么一个 provider 往往会在两个 assembly attribute 上各写一遍，或者直接实现两个 builder 接口。

### 5.3 `IProviderBuilder<TBuilder>` 是 provider 真正的装配入口

这个接口只有一个方法：

- `Configure(TBuilder builder, string? name, IConfigurationSection configurationSection)`

它做的事很直接：

- 从配置节读取参数
- 调用具体的 `builder.Add...` 或 `builder.Use...`
- 把 provider 从“名字”翻译成容器注册

看一个例子就很清楚：

`MemoryGrainStorageProviderBuilder.Configure(...)` 最后会调用：

- `builder.AddMemoryGrainStorage(...)`

`ConsulClusteringProviderBuilder.Configure(...)` 会调用：

- `builder.UseConsulSiloClustering(...)`
- `builder.UseConsulClientClustering(...)`

`MemoryStreamProviderBuilder.Configure(...)` 会调用：

- `builder.AddMemoryStreams(name)`

这说明 `IProviderBuilder` 不是 provider 本体，而是 provider 装配脚本。

### 5.4 配置文件里的 `ProviderType` 只是一个字符串门牌号

`DefaultSiloServices` / `DefaultClientServices` 在读配置时，会看：

- section name
- child name
- `ProviderType` 字段

它会把这些字符串拼成一个 lookup key，再去找上面那个 provider 字典。

如果找不到，就直接抛错，并且把当前 kind 下有哪些 provider 名字列出来。

这个报错很实在，但也暴露了 Orleans 的一个特点：

它的 provider 发现和装配，本质上是字符串驱动的。

### 5.5 具体 provider 的注册流程很像流水线

以 `MemoryGrainStorageProviderBuilder` 为例：

1. assembly 上打 `RegisterProvider("Memory", "GrainStorage", "Silo", ...)`
2. `DefaultSiloServices.ApplyConfiguration(...)` 扫到它
3. 运行时在 `GrainStorage` 配置节里看到 `ProviderType = "Memory"`
4. 实例化 `MemoryGrainStorageProviderBuilder`
5. 调 `Configure(...)`
6. 最后落到 `builder.AddMemoryGrainStorage(...)`

这条链很完整，也很“总装配式”。

---

## 6. 为什么 Orleans 会自然长成“总装配式架构”

这一段是我觉得最值得单独讲的。

### 6.1 因为 Orleans 的默认行为太多了

普通框架通常只帮你装一部分基础设施。

Orleans 不一样，它默认就把这些东西都塞进去：

- serializing
- grain reference creation
- membership
- placement
- directory
- timers
- reminders
- persistence
- streaming
- networking
- lifecycle
- versioning

这意味着 builder 不是简单的“扩展点”，而是系统组装中枢。

### 6.2 因为它同时支持静态发现和运行时发现

Orleans 有两套 discovery：

一套是编译期 / assembly 级：

- `ApplicationPartAttribute`
- `TypeManifestProviderAttribute`

另一套是运行时 provider 级：

- `RegisterProviderAttribute`
- `IProviderBuilder<TBuilder>`

这两套都合法，也都在用。

好处是灵活。

坏处是看代码时很容易不知道一个服务到底是：

- 编译生成出来的
- 默认注册出来的
- 配置发现出来的
- 还是某个 package 自己塞进去的

### 6.3 因为很多功能都要同时服务于 silo 和 client

Orleans 很多核心对象不是“服务器专属”或者“客户端专属”那么简单。

比如：

- `GrainFactory`
- `GrainReferenceActivator`
- `IGrainReferenceRuntime`
- `CodecProvider`
- `TypeConverter`
- `MessageFactory`

这些在 client 和 silo 两边都要有。

于是装配表就会越来越大，默认服务方法也越来越长。

### 6.4 因为 provider 配置是字符串驱动的

`Clustering`、`Reminders`、`BroadcastChannel`、`Streaming`、`GrainStorage`、`GrainDirectory` 这些 section 名字，本身就是一套协议。

再加上 provider 的 `Kind` / `Name` / `Target`，整个装配流程很容易变成：

- 先读字符串
- 再找 attribute
- 再找 type
- 再实例化 builder
- 再让 builder 自己去调注册方法

这当然灵活，但绝对不算干净。

---

## 7. 我觉得这块最不够干净的地方

### 7.1 `DefaultSiloServices` / `DefaultClientServices` 太像“上帝方法”

它们同时做了：

- 默认服务注册
- 运行时核心装配
- 领域模块装配
- provider 发现入口
- 配置绑定入口
- 生命周期挂载入口

单个方法太厚，阅读成本很高。

### 7.2 `ApplicationPart` 和 `RegisterProvider` 是两条并行的发现系统

这两个系统目标不同，但都依赖 assembly attribute、反射和相关程序集扫描。

结果就是：

- 你得先知道哪种 feature 走哪条发现链
- 你还得知道这个 assembly 是否已经被 `ReferencedAssemblyProvider` 视为 relevant
- 最后还得知道配置是否真的把这个 provider 选中了

这个心智负担不小。

### 7.3 字符串驱动的 `kind / name / target / section` 容易把错误拖到运行时

很多问题不会在编译期炸。

要到运行时配置加载时才报：

- provider 找不到
- target 不匹配
- provider builder 类型不对
- 配置 section 结构不对

这对框架使用者不算友好，但对 Orleans 来说是历史包袱和灵活性一起留下来的结果。

### 7.4 Client 和 Silo 的默认装配有很强的镜像感，但并不完全对称

这会让人误以为 Orleans 的两端是同一套模块的薄厚差异。

实际上不是。

有些服务两端都在，只是角色不同；有些只存在于一端；还有一些虽然两端都有，却是通过完全不同的默认链装进去的。

这就很容易在复刻时把边界做歪。

---

## 8. 如果你要自己复刻，我会怎么拆

如果目标是复刻一版更干净的 Orleans，我会把这一层拆成四块。

### 8.1 Core Defaults

只负责最基础的 runtime 装配：

- logging
- options
- lifecycle
- serializer
- message factory
- type metadata

不要把领域模块也塞进去。

### 8.2 Feature Modules

把这些拆成独立 feature：

- placement
- directory
- membership
- reminders
- timers
- streaming
- persistence

每个 feature 模块都只暴露一两个明确入口，而不是把默认注册堆进一个巨大的 `AddDefaultServices`。

### 8.3 Provider Registry

把 provider discovery 变成一层显式 registry：

- `kind`
- `name`
- `target`
- `builder type`

然后配置解析只负责查 registry，不要再到处反射 assembly attribute。

### 8.4 Manifest Discovery

把编译期生成出来的 manifest 和运行时 provider registry 分开。

一个负责代码生成产物的挂载。

一个负责 provider 的运行时注册。

这俩不要混成一锅。

---

## 9. 推荐的阅读顺序

如果你要把这一层真正吃透，我建议按这个顺序读：

1. `src/Orleans.Runtime/Hosting/SiloBuilder.cs`
2. `src/Orleans.Core/Core/ClientBuilder.cs`
3. `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`
4. `src/Orleans.Core/Core/DefaultClientServices.cs`
5. `src/Orleans.Serialization/Hosting/ServiceCollectionExtensions.cs`
6. `src/Orleans.Serialization/Hosting/SerializerBuilderExtensions.cs`
7. `src/Orleans.Serialization/Hosting/ReferencedAssemblyProvider.cs`
8. `src/Orleans.Serialization.Abstractions/Annotations.cs`
9. `src/Orleans.Core.Abstractions/Providers/IProviderBuilder.cs`
10. `src/Orleans.Persistence.Memory/Hosting/MemoryGrainStorageProviderBuilder.cs`
11. `src/Orleans.Clustering.Consul/ConsulClusteringProviderBuilder.cs`
12. `src/Orleans.Streaming/Hosting/StreamingServiceCollectionExtensions.cs`
13. `src/Orleans.Streaming/MemoryStreams/MemoryStreamProviderBuilder.cs`
14. `src/Orleans.Reminders/Hosting/MemoryReminderTableBuilder.cs`

如果你把这条顺一遍，再回头看前面那些“功能模块是怎么被挂进来的”，很多东西就会从黑盒变成明牌。

---

## 10. 这一篇的落点

把这一篇压成一句话，就是：

Orleans 的默认服务注册不是附属层，而是架构本身的一部分。

它把 runtime、serializer、directory、membership、placement、streaming、reminder、persistence、provider discovery 全都压进 builder 装配阶段，所以它的默认行为很强，插件生态也很活，但代价就是：

- 总装配表很厚
- 发现链很长
- 边界不总是清楚
- 读源码时很容易把“默认注册”和“领域模块装配”混在一起

如果后面继续写，我建议下一篇直接接：

- `Orleans 的扩展点地图：哪些地方适合做 provider，哪些地方其实不该再做总装配`

这样可以把前面几篇的源码链条，最后收成一张真正能用的复刻地图。
