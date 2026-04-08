# 测试基础设施与 TestCluster：Orleans 的测试怎么搭、怎么拆、怎么反推架构边界（第十七篇）

这一篇专门讲 Orleans 自己的测试基建。

如果只从外面看，`TestCluster` 很像一个“帮你起一个小集群”的工具类。但源码看下来，它远不止这个。它其实把几件事绑在了一起：

- 集群怎么启动
- silo 怎么起、怎么停、怎么重启
- client 怎么连
- 测试时怎么替换 membership、directory、transport、storage provider
- 怎么从外部把本该很难测的内部状态拿出来

先把结论说清楚：

- `TestCluster` 是最常用的集成测试入口，负责把一个完整 Orleans cluster 搭起来。
- `InProcessTestCluster` 是另一条更轻的测试路线，把 membership、directory 和 transport 都替换成进程内实现。
- `SiloHandle` 是统一的 silo 生命周期抽象，进程内和进程外启动方式都靠它收口。
- `TestClusterHostFactory` 把“测试用 host 怎么构造”这件事集中起来，顺手把测试专用 service 和 `TestHooksSystemTarget` 塞进去。
- `TestHooks` 不是为了偷看私有字段，而是为了让测试能安全地触达那些正常运行时不该暴露的视图。
- 测试 provider 不是单一概念，而是一整套替身和装饰器：内存 membership、内存 grain directory、in-memory transport、故障注入 storage、测试诊断收集器。

如果用一句话概括：

> Orleans 的测试体系不是“在真系统外面包一层 shell”，而是专门搭了一套可替换基础设施，让你能把 runtime 的边界一块一块掰开测。

---

## 1. 先把整条测试链压成一张图

```text
测试代码
  -> TestClusterBuilder / InProcessTestClusterBuilder
  -> TestClusterOptions / InProcessTestClusterOptions
  -> TestDefaultConfiguration
  -> TestClusterHostFactory
  -> SiloHandle / InProcessSiloHandle / StandaloneSiloHandle
  -> TestCluster / InProcessTestCluster
  -> 真实 or in-process silo
  -> ClientHost / IClusterClient
  -> TestHooksSystemTarget / DiagnosticEventCollector / fake providers

可替换基础设施
  -> InMemoryMembershipTable / InProcessMembershipTable
  -> InMemoryGrainDirectory / InProcessGrainDirectory
  -> InMemoryTransport
  -> FaultInjectionGrainStorage
  -> Memory storage / reminder / durable jobs
  -> TestHooksEnvironmentStatisticsProvider
```

这张图里最重要的点是：

测试不是只替换一个 mock provider。

它替换的是一整条链，包括：

- 集群身份
- endpoint 和 transport
- membership/directory 事实源
- client 连接方式
- 诊断和测试 hook

所以 Orleans 的测试基础设施，本质上是在复刻 runtime 的边界，而不是单纯做依赖注入。

---

## 2. `TestCluster` 是怎么搭起来的

关键文件：

- `src/Orleans.TestingHost/TestClusterBuilder.cs`
- `src/Orleans.TestingHost/TestCluster.cs`
- `src/Orleans.TestingHost/SiloHandle.cs`
- `src/Orleans.TestingHost/InProcessSiloHandle.cs`
- `src/Orleans.TestingHost/StandaloneSiloHandle.cs`

### 2.1 `TestClusterBuilder` 先把默认值铺好

`TestClusterBuilder` 的默认构造会先设一组测试友好的选项：

- `InitialSilosCount = 2`
- `UseTestClusterMembership = true`
- `InitializeClientOnDeploy = true`
- `ConfigureFileLogging = true`
- `AssumeHomogenousSilosForTesting = true`

然后它还会默认加两个 configurator：

- `ConfigureDistributedGrainDirectory`
- `ConfigureStaticClusterDeploymentOptions`

这说明 Orleans 的测试 cluster 不是“空白画布”，而是默认就帮你开了几个测试模式。

### 2.2 `TestClusterOptions` 把测试场景参数化了

关键文件：

- `src/Orleans.TestingHost/TestClusterOptions.cs`

这里装的是测试 cluster 的运行参数：

- `ClusterId`
- `ServiceId`
- `BaseSiloPort`
- `BaseGatewayPort`
- `UseTestClusterMembership`
- `InitializeClientOnDeploy`
- `InitialSilosCount`
- `GatewayPerSilo`
- `ConnectionTransport`
- `SiloBuilderConfiguratorTypes`
- `ClientBuilderConfiguratorTypes`

它还有一个很关键的动作：

如果启用了 `UseTestClusterMembership`，它会把 clustering provider 直接改成 `Development`。

这相当于告诉 runtime：

“别去连真实外部 membership 了，这次就是测试环境。”

### 2.3 `TestClusterHostFactory` 负责把配置翻译成 host

关键文件：

- `src/Orleans.TestingHost/TestClusterHostFactory.cs`

`TestClusterHostFactory` 是测试 host 的总工厂。

它做两件事：

1. 构造 silo host
2. 构造 client host

而且它不是“只创建 host”，还会顺手把测试配置注入进去：

- `ConfigureAppServices(...)`
- `ConfigureClientAppServices(...)`
- `TryConfigureFileLogging(...)`
- `InitializeTestHooksSystemTarget(...)`

这里最值得注意的是：

它会读取 `SiloBuilderConfiguratorTypes` 和 `ClientBuilderConfiguratorTypes`，用反射把测试里声明的 configurator 找出来，然后分别喂给 silo builder 和 client builder。

也就是说，测试里写一个 configurator 类，最后会被当成 runtime 装配的一部分执行。

### 2.4 `SiloHandle` 把进程内和进程外的 silo 统一了

关键文件：

- `src/Orleans.TestingHost/SiloHandle.cs`
- `src/Orleans.TestingHost/InProcessSiloHandle.cs`
- `src/Orleans.TestingHost/StandaloneSiloHandle.cs`

`SiloHandle` 是统一抽象。

不管 silo 是：

- 跟测试进程同进程同 AppDomain
- 还是单独起一个进程

它都用同一个 handle 接口去表示：

- `Name`
- `SiloAddress`
- `GatewayAddress`
- `IsActive`
- `StopSiloAsync(...)`
- `DisposeAsync()`

`InProcessSiloHandle` 直接拿 `IHost`，`StandaloneSiloHandle` 通过外部进程和标准输出握手。

这说明 Orleans 的测试体系不是“只有一种启动方式”，而是专门把生命周期接口抽象出来，让不同部署方式共享同一套测试逻辑。

---

## 3. `TestCluster` 的真实链路

关键文件：

- `src/Orleans.TestingHost/TestCluster.cs`

### 3.1 部署的时候，先起 silo，再起 client

`DeployAsync()` 的顺序很直接：

1. 启动一组 silo
2. 如果配置了 `InitializeClientOnDeploy`，再启动 client
3. 等集群稳定

它不是先起 client 再等 cluster，就因为测试里更常见的是“先有一个可用集群，再把请求打进去”。

### 3.2 silo 是按 handle 管理的

`TestCluster` 持有：

- `Primary`
- `SecondarySilos`
- `Silos`

每个 silo 都是一个 `SiloHandle`。

所以测试里可以很方便地做这些事：

- 新起一个 silo
- 停掉一个 silo
- 杀掉一个 silo 模拟 crash
- 重启一个 silo

这已经不是普通的“单机测试”，而是在手工操控一个小集群。

### 3.3 client 也不是黑盒

`TestCluster` 会构造一个 `ClientHost`，再把它暴露成：

- `Client`
- `GrainFactory`
- `ServiceProvider`

测试代码最后拿到的不是“某个神秘测试客户端”，而是和 runtime 一样的 `IClusterClient` 和 `IGrainFactory`。

这点很重要。

它说明 Orleans 的测试不是另起一套调用模型，而是尽量复用生产路径。

### 3.4 它还提供了几个很实用的测试动作

`TestCluster` 直接内建了这些操作：

- `StartAdditionalSiloAsync()`
- `StopSiloAsync(...)`
- `KillSiloAsync(...)`
- `RestartSiloAsync(...)`
- `WaitForDeactivationAsync(...)`
- `DeactivateAsync(...)`
- `MigrateAsync(...)`
- `WaitForLivenessToStabilizeAsync(...)`

这比单纯“能启动一个 cluster”强很多。

你可以直接在测试里控制 activation、故障、迁移、回收、membership 稳定性。

---

## 4. In-process 测试集群更像一个可替换 runtime

关键文件：

- `src/Orleans.TestingHost/InProcTestClusterBuilder.cs`
- `src/Orleans.TestingHost/InProcTestCluster.cs`
- `src/Orleans.TestingHost/InProcess/InProcessMembershipTable.cs`
- `src/Orleans.TestingHost/InProcess/InProcessGrainDirectory.cs`
- `src/Orleans.TestingHost/InMemoryTransport/InMemoryTransportListenerFactory.cs`

### 4.1 `InProcessTestCluster` 把 membership 和 directory 都换成了内存实现

这里最核心的是：

- `InProcessMembershipTable`
- `InProcessGrainDirectory`

这两个替换掉了真实集群里最麻烦的两层外部依赖。

`InProcessMembershipTable` 既实现 `IMembershipTable`，又实现 `IGatewayListProvider`。

`InProcessGrainDirectory` 则直接把 `GrainId -> GrainAddress` 放进并发字典里。

这样一来，测试 cluster 不需要外部数据库，也不需要真实分布式目录，就能把 membership、gateway 和 directory 的逻辑跑起来。

### 4.2 transport 也有内存版

关键文件：

- `src/Orleans.TestingHost/InMemoryTransport/InMemoryTransportListenerFactory.cs`
- `src/Orleans.TestingHost/InMemoryTransport/InMemoryTransportConnection.cs`

内存 transport 不是 mock。

它是用 `DuplexPipe` 和连接 listener 真正搭了一条本地通信管道。

这意味着测试里的消息路径并没有被“假调用”简化掉，只是把 socket 换成了进程内管道。

这很适合测：

- 连接生命周期
- gateway 选择
- 消息收发
- shutdown / reconnect / abort

### 4.3 `InProcessTestCluster` 比普通 `TestCluster` 更适合白盒测试

它提供了：

- 直接拿 `ActivationDirectory` 找 activation
- `WaitForDeactivationAsync(...)`
- `DeactivateAsync(...)`
- `MigrateAsync(...)`
- `WaitForLivenessToStabilizeAsync(...)`

因为它和 runtime 更贴近，所以很多本来难测的内部行为都能从测试侧直接观测。

这也是为什么 Orleans 自己的测试基建，反过来能暴露出 runtime 的真实边界：

- 哪些东西必须可替换
- 哪些状态必须可观测
- 哪些操作必须能被强制触发

---

## 5. `TestHooks` 是测试能“看见内部”的关键

关键文件：

- `src/Orleans.Runtime/Silo/TestHooks/ITestHooksSystemTarget.cs`
- `src/Orleans.Runtime/Silo/TestHooks/TestHooksSystemTarget.cs`
- `src/Orleans.TestingHost/ClientExtensions.cs`

### 5.1 这是一个故意开放的系统目标

`TestHooksSystemTarget` 是 runtime 里专门留给测试的系统目标。

它提供的能力很直接：

- 查一致性环主节点
- 查 service id
- 查是否有某个 storage provider
- 查是否有某个 stream provider
- 查询 approximate silo statuses
- 强制注销 grain 用于测试

这些接口不是给普通业务代码用的。

它的存在本身就说明 Orleans 的测试文化偏白盒，而不是纯黑盒。

### 5.2 client 通过 `GetTestHooks(...)` 访问它

`ClientExtensions.GetTestHooks(...)` 会直接按 silo address 去找这个 system target。

这很关键。

它不是通过 gateway 地址绕一层，而是直接针对目标 silo 的 system target。

这样测试可以在不碰业务 grain 的情况下，直接读内部视图。

### 5.3 这类 hook 也暴露了 Orleans 的边界

如果一个运行时连测试都没法问清楚：

- 当前集群视图是什么
- 某个 provider 有没有注册
- 某个 grain 是否已经注销

那它本身的模块边界往往就不够清楚。

所以 `TestHooks` 的存在不是小技巧，而是架构可测性的外露接口。

---

## 6. 测试 provider 不是一个东西，而是一组替身和装饰器

关键文件：

- `src/Orleans.TestingHost/TestStorageProviders/FaultInjectionStorageProvider.cs`
- `src/Orleans.TestingHost/TestStorageProviders/FaultInjectionStorageServiceCollectionExtensions.cs`
- `src/Orleans.TestingHost/TestClusterExtensions.cs`
- `test/TestInfrastructure/TestExtensions/TestDefaultConfiguration.cs`

### 6.1 storage provider 可以做故障注入

`FaultInjectionGrainStorage` 是一个很典型的测试 provider。

它包在真实 `IGrainStorage` 外层，写之前、读之前、清之前都可以：

- 加延迟
- 触发测试 grain 的 hook
- 人为抛异常

这能拿来测：

- 错误恢复
- 重试
- 并发冲突
- state read/write 失败后的行为

### 6.2 测试 storage 不是“换个 memory storage”这么简单

`AddFaultInjectionMemoryStorage(...)` 说明 testing host 里有一套专门的注册方式，能把一个 storage provider 包成 fault injection 版本，再挂到 silo 里。

这比单纯直接用 memory storage 更有用，因为它保留了真实 provider 的接口形状。

### 6.3 默认测试配置把外部依赖都藏起来了

`TestDefaultConfiguration` 做的事情很实在：

- 从环境变量或上层目录找测试 secret
- 统一配置默认集群参数
- 统一配置 host configuration

这让测试代码本身更干净。

你在测试里关心的是运行时行为，不是每次都手写一大坨连接串。

### 6.4 诊断事件收集器也是测试工具的一部分

`DiagnosticEventCollector` 能订阅 `DiagnosticListener.AllListeners`，再按事件名等事件。

这类工具很适合替代 `Sleep` 和“拍脑袋等一会儿”。

它让测试能直接等待某个 runtime 事件出现，可靠性会高很多。

---

## 7. Orleans 的测试体系为什么能反推架构边界

这一段是整篇最值得记的。

因为 Orleans 的测试不是附属物，它自己就把架构边界暴露出来了。

### 7.1 哪些东西必须可替换

从测试基建就能看出来，下面这些必须能换：

- membership table
- grain directory
- connection transport
- storage provider
- reminder / durable jobs / stream provider
- environment statistics

如果这些不能替换，测试就很难写，runtime 也会被外部依赖绑死。

### 7.2 哪些东西必须可观测

测试里反复在问：

- activation 在哪
- silo 是否 active
- 集群视图是否稳定
- 某个 provider 是否挂上
- 某个 grain 是否已经注销

这说明 runtime 里必须有一批可观测点，不然测试只能靠猜。

### 7.3 哪些能力必须能被强制触发

比如：

- `DeactivateAsync`
- `MigrateAsync`
- `KillSiloAsync`
- `RestartSiloAsync`

这些不是业务功能，但它们是测试和故障验证必须要有的控制面。

如果没有这些控制面，很多边界行为就根本测不住。

### 7.4 这也暴露了 Orleans 的一点不够干净

测试能力很强，但它是靠一堆专门入口拼出来的：

- `TestCluster`
- `InProcessTestCluster`
- `TestHooksSystemTarget`
- `TestClusterHostFactory`
- 各类 fake provider

这说明测试友好性不是顺手得到的，而是后来专门补出来的一层。

换句话说，Orleans 的 runtime 边界并不是天然清晰，而是靠测试基建反复磨出来的。

---

## 8. 如果你要自己复刻，我会怎么拆

如果目标是复刻一套更干净的测试基建，我会拆成四层。

### 8.1 Cluster Harness

只负责：

- 启动 cluster
- 启动 client
- 管理 handle
- 控制生命周期

### 8.2 Test Environment Adapters

只负责替换外部依赖：

- membership
- directory
- transport
- storage
- statistics

### 8.3 White-box Hooks

只负责可观测性：

- 集群视图
- activation 状态
- provider 状态
- 目录和环状态

### 8.4 Test Utilities

只负责测试便利性：

- default config
- diagnostic collector
- wait helpers
- deactivation/migration helpers

这样拆完以后，测试会更像一个独立产品层，而不是 runtime 里长出来的一堆附属代码。

---

## 9. 推荐阅读顺序

如果你想顺着源码往下读，我建议按这个顺序：

1. `src/Orleans.TestingHost/TestClusterBuilder.cs`
2. `src/Orleans.TestingHost/TestCluster.cs`
3. `src/Orleans.TestingHost/TestClusterHostFactory.cs`
4. `src/Orleans.TestingHost/TestClusterOptions.cs`
5. `src/Orleans.TestingHost/SiloHandle.cs`
6. `src/Orleans.TestingHost/InProcessSiloHandle.cs`
7. `src/Orleans.TestingHost/StandaloneSiloHandle.cs`
8. `src/Orleans.TestingHost/InProcTestClusterBuilder.cs`
9. `src/Orleans.TestingHost/InProcTestCluster.cs`
10. `src/Orleans.Runtime/Silo/TestHooks/TestHooksSystemTarget.cs`
11. `src/Orleans.Runtime/Silo/TestHooks/ITestHooksSystemTarget.cs`
12. `src/Orleans.TestingHost/TestStorageProviders/FaultInjectionStorageProvider.cs`
13. `src/Orleans.TestingHost/TestStorageProviders/FaultInjectionStorageServiceCollectionExtensions.cs`
14. `test/TestInfrastructure/TestExtensions/TestDefaultConfiguration.cs`
15. `test/TestInfrastructure/TestExtensions/TestClusterPerTest.cs`
16. `test/TestInfrastructure/TestExtensions/BaseInProcessTestClusterFixture.cs`
17. `test/TestInfrastructure/TestExtensions/HostedTestClusterBase.cs`
18. `src/Orleans.TestingHost/Diagnostics/DiagnosticEventCollector.cs`

---

## 10. 这一篇的落点

把这一篇压成一句话，就是：

Orleans 的测试基建不是附加工具，而是 runtime 边界的一面镜子。

它逼着系统把下面这些东西讲清楚：

- 哪些依赖是可替换的
- 哪些状态是可观测的
- 哪些故障是可控的
- 哪些生命周期动作必须能被测试直接驱动

所以你如果想复刻一版更干净的 Orleans，测试层其实是很好的切入口。

因为测试能不能顺手做，往往直接暴露出 runtime 到底是不是模块分明。

