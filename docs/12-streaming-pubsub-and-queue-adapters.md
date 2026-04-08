# Streaming、PubSub 与 Queue Adapter：流是怎么投递、恢复和分发的（第十二篇）

这一篇专门讲 Orleans 的 streaming。

Streaming 这块比看起来复杂很多。名字上它像“发消息”，但实际上它同时牵着四条线：

- stream 本体怎么表示
- pub/sub 怎么登记订阅关系
- queue adapter 怎么从底层取消息
- pulling agent 怎么把消息真正拉上来并派发

先把结论说清楚：

- `IStreamProviderRuntime` 是 streaming provider 的运行时面。
- `PersistentStreamProvider` 是持久流的总入口。
- `SiloStreamProviderRuntime` / `ClientStreamingProviderRuntime` 把 silo 侧和 client 侧的 streaming 运行时拆开。
- `IQueueAdapterFactory` / `IQueueAdapter` / `IQueueAdapterCache` 是持久流的底层通道。
- `QueueBalancerBase` 根据 membership 视图调整 queue 分布。
- `PubSubRendezvousGrain` 负责把 producer / consumer 的订阅关系持久化。
- `MemoryAdapterFactory` 代表 in-memory stream 的一条完全不同的实现路径，它更像测试和简单场景用的本地队列。

如果用一句话概括：

> Orleans 的 streaming 不是一条链，而是“provider runtime + pubsub + queue adapter + balancer + pulling agent”拼起来的一套系统。

---

## 1. 先把整条链压成一张图

```text
builder.AddStreaming()
  -> AddSiloStreaming / AddClientStreaming
  -> 注册 IStreamProviderRuntime
  -> 注册 pubsub / stream directory / queue balancer / adapter factory / pulling manager

持久流
  -> SiloPersistentStreamConfigurator
  -> PersistentStreamProvider
  -> IQueueAdapterFactory
  -> IQueueAdapter
  -> IPersistentStreamPullingManager
  -> StreamPubSub
  -> PubSubRendezvousGrain

取流
  -> IStreamProvider.GetStream
  -> StreamDirectory
  -> StreamImpl<T>
  -> producer / consumer interface
  -> queue adapter / pubsub / pulling agent

memory stream
  -> MemoryStreamProviderBuilder
  -> MemoryAdapterFactory<TSerializer>
  -> MemoryMessageBody
  -> MemoryBatchContainer<TSerializer>
```

这张图里最容易看错的地方有两个。

第一，stream provider 不是单一对象，它实际上是一个装配好的运行时组件集合。

第二，persistent stream 和 memory stream 走的不是同一条实现路径，虽然它们对外都叫 stream provider。

---

## 2. 先看默认装配是怎么把 streaming 挂上的

关键文件：

- `src/Orleans.Streaming/Hosting/StreamingServiceCollectionExtensions.cs`
- `src/Orleans.Streaming/Hosting/SiloBuilderStreamingExtensions.cs`
- `src/Orleans.Streaming/Hosting/ClientBuilderStreamingExtensions.cs`

### 2.1 `AddStreaming()` 只是入口

`SiloBuilder.AddStreaming()` 和 `ClientBuilder.AddStreaming()` 本身很薄。

它们最终会调用：

- `services.AddSiloStreaming()`
- `services.AddClientStreaming()`

### 2.2 Silo 和 Client 的 streaming 装配不一样

Silo 侧会装：

- `PubSubGrainStateStorageFactory`
- `SiloStreamProviderRuntime`
- `ImplicitStreamSubscriberTable`
- `IStreamQueueBalancer`
- `StreamConsumerGrainContextAction`
- `StreamDirectory`

Client 侧会装：

- `ClientStreamingProviderRuntime`
- `ImplicitStreamSubscriberTable`
- `StreamSubscriptionManagerAdmin`

Silo 侧会拉起 pulling agents。

Client 侧不会。

这说明 client streaming 只负责消费侧 runtime，而持久流的生产 / 拉取 / 分发真正是在 silo 侧完成的。

---

## 3. `IStreamProviderRuntime` 是 streaming provider 的 runtime 面

关键文件：

- `src/Orleans.Streaming/Providers/IStreamProviderRuntime.cs`
- `src/Orleans.Streaming/Providers/SiloStreamProviderRuntime.cs`
- `src/Orleans.Streaming/Providers/ClientStreamingProviderRuntime.cs`

这个接口把 streaming runtime 需要的几个能力放在了一起：

- `ExecutingEntityIdentity()`
- `GetStreamDirectory()`
- `PubSub(StreamPubSubType)`

如果是 silo 侧，还会额外提供：

- `InitializePullingAgents(...)`

### 3.1 `SiloStreamProviderRuntime` 比 client 侧重得多

它要负责：

- pub/sub 模式选择
- queue balancer 创建
- backoff provider 选择
- pulling agent manager 初始化
- stream directory 获取
- grain extension 绑定

也就是说，silo 侧不只是“能发 stream”，而是完整持久流控制面。

### 3.2 `ClientStreamingProviderRuntime` 是轻量版

它主要负责：

- stream directory
- pub/sub 视图
- client activation 侧 extension 绑定
- lifecycle cleanup

它不会拉起 pulling agents。

这也是为什么 client streaming 看起来像“能订阅流”，但真正的持续投递和 queue 调度逻辑仍然在 silo 侧。

---

## 4. 持久流的总入口是 `PersistentStreamProvider`

关键文件：

- `src/Orleans.Streaming/PersistentStreams/PersistentStreamProvider.cs`
- `src/Orleans.Streaming/SiloPersistentStreamConfigurator.cs`

### 4.1 `SiloPersistentStreamConfigurator` 先把默认 streaming 配件装进去

这个 configurator 会：

- 调 `services.AddSiloStreaming()`
- 注册 `PersistentStreamProvider`
- 让 provider 参与 lifecycle
- 注册 adapter factory
- 注册 pub/sub 存储配置校验器

它的意思很明确：

持久流不是只加一个 provider 类型，而是把一整套依赖一起挂上。

### 4.2 `PersistentStreamProvider` 自己不直接拉消息

它持有的核心对象是：

- `IQueueAdapterFactory`
- `IQueueAdapter`
- `IPersistentStreamPullingManager`
- `IStreamSubscriptionManager`
- `StreamPubSubOptions`
- `StreamLifecycleOptions`

初始化时，它会：

1. 从 service provider 里拿 adapter factory
2. 创建 queue adapter
3. 按 pubsub type 选择订阅管理器
4. 在 start 阶段初始化 pulling agents

这说明 provider 本身更像一个 orchestrator。

### 4.3 它的生命周期是分阶段的

`Init -> Start -> Close`

这三步分别做不同的事：

- `Init` 准备 adapter 和订阅管理器
- `Start` 拉起 pulling agent
- `Close` 停掉 pulling agent

所以持久流在 Orleans 里不是一个纯静态对象，而是一个有生命周期的系统组件。

---

## 5. queue adapter 才是真正接底层消息的地方

关键文件：

- `src/Orleans.Streaming/QueueAdapters/IQueueAdapterFactory.cs`
- `src/Orleans.Streaming/MemoryStreams/MemoryAdapterFactory.cs`

### 5.1 `IQueueAdapterFactory` 定义了底层能力边界

它要能提供：

- `CreateAdapter()`
- `GetQueueAdapterCache()`
- `GetStreamQueueMapper()`
- `GetDeliveryFailureHandler(...)`

也就是说，它不只是“工厂”，它还负责告诉系统：

- queue 怎么映射
- message cache 怎么走
- delivery failure 怎么处理

### 5.2 `MemoryAdapterFactory` 是最容易看懂的一条实现

它把很多角色都揽在自己身上：

- factory
- adapter
- cache
- receiver 创建器

它的行为很直接：

1. 把 `MemoryMessageBody` 先序列化成字节
2. 根据 streamId 找 queueId
3. 找到对应的 in-memory queue grain
4. 把消息 enqueue 进去

这说明 memory stream 更像一个“进程内模拟出来的事件队列”，适合测试和简单场景。

### 5.3 `MemoryMessageBody` 和 `MemoryBatchContainer` 把事件和 request context 一起封装起来

`MemoryMessageBody` 包两样东西：

- `Events`
- `RequestContext`

`MemoryBatchContainer<TSerializer>` 再把它包装成 `IBatchContainer`。

所以 memory stream 的消息体不是纯 event 列表，它还保留了 Orleans 的 request context 语义。

这也解释了为什么 memory stream 在测试里很有用：

它不只是“能传事件”，而是能更接近真实消息链路。

---

## 6. pub/sub 关系是怎么落地的

关键文件：

- `src/Orleans.Streaming/PubSub/PubSubRendezvousGrain.cs`
- `src/Orleans.Streaming/Providers/SiloStreamProviderRuntime.cs`
- `src/Orleans.Streaming/SiloPersistentStreamConfigurator.cs`

### 6.1 `PubSubRendezvousGrain` 是订阅关系的持久化中心

它持有的状态很小：

- `Producers`
- `Consumers`

这些状态由 `StateStorageBridge<PubSubGrainState>` 持久化。

也就是说，stream 的订阅关系不是存在某个临时缓存里，而是会落到 grain storage。

### 6.2 `PubSubGrainStateStorageFactory` 会按 stream provider 名字挑 storage

它会从 `PubSubRendezvousGrain` 的 grain id 里解析 provider name，然后尝试找同名的 `IGrainStorage`。

找不到就退回默认 pubsub storage provider。

这是一条很典型的 Orleans 链路：

- provider 名字既是配置名
- 也是 routing key
- 还是 storage key

灵活，但不算干净。

### 6.3 `StreamPubSubType` 决定走哪种 pub/sub

`SiloStreamProviderRuntime.PubSub(...)` 会根据 `StreamPubSubType` 返回：

- explicit grain-based only
- explicit + implicit
- implicit only

这说明 pub/sub 不是一个实现，而是一个策略切换点。

---

## 7. `QueueBalancerBase` 为什么要盯 membership

关键文件：

- `src/Orleans.Streaming/QueueBalancer/QueueBalancerBase.cs`
- `src/Orleans.Streaming/Providers/SiloStreamProviderRuntime.cs`

`QueueBalancerBase` 会直接订阅 `IClusterMembershipService.MembershipUpdates`。

它这样做的原因很简单：

queue 分布不是静态的，active silo 变了以后，queue 归属就要跟着动。

所以它会：

1. 监听 membership 更新
2. 取 active silo 列表
3. 发现 active 集合变化后通知监听器

这块和前一篇的 membership 是直接连着的。

也就是说，streaming 的 queue 分配不是“单独调度”，而是集群视图驱动的。

---

## 8. 这条线里我觉得最不够干净的地方

### 8.1 一个 `Name` 同时被拿来做很多事

同一个 stream provider name 会被当成：

- provider 配置名
- queue adapter 名
- pub/sub 存储名
- keyed service 名
- pulling agent manager 名字的一部分

这会让问题排查时很绕。

### 8.2 Provider、adapter、pub/sub、runtime 混得很深

从外面看 streaming 是一个功能。

从源码看它其实是五层：

- provider runtime
- provider
- adapter factory
- queue adapter
- pub/sub store

边界不算锋利。

### 8.3 memory stream 和 persistent stream 共享太多壳，但语义差很多

它们都叫 stream provider，但可靠性、生命周期、底层通道完全不同。

如果不仔细分，很容易把它们当成同一种东西。

### 8.4 pub/sub 既像状态，又像协议

`PubSubRendezvousGrain` 既在存状态，又在协调 producer / consumer 的变化。

这会让它看起来像一个很重的中枢对象。

---

## 9. 如果你要自己复刻，我会怎么拆

### 9.1 Stream Runtime

只负责 provider 级 runtime 面，不负责底层 queue 细节。

### 9.2 Adapter Layer

只负责 queue/message 的生产和消费。

### 9.3 Pub/Sub Registry

只负责订阅关系，不掺 pulling agent。

### 9.4 Delivery Orchestrator

只负责 pulling agent、queue balancer、backoff、重试策略。

这样拆以后，测试和调试都会清楚很多。

---

## 10. 推荐阅读顺序

建议按这个顺序读：

1. `src/Orleans.Streaming/Hosting/StreamingServiceCollectionExtensions.cs`
2. `src/Orleans.Streaming/Hosting/SiloBuilderStreamingExtensions.cs`
3. `src/Orleans.Streaming/Hosting/ClientBuilderStreamingExtensions.cs`
4. `src/Orleans.Streaming/Providers/IStreamProviderRuntime.cs`
5. `src/Orleans.Streaming/Providers/SiloStreamProviderRuntime.cs`
6. `src/Orleans.Streaming/Providers/ClientStreamingProviderRuntime.cs`
7. `src/Orleans.Streaming/SiloPersistentStreamConfigurator.cs`
8. `src/Orleans.Streaming/PersistentStreams/PersistentStreamProvider.cs`
9. `src/Orleans.Streaming/QueueAdapters/IQueueAdapterFactory.cs`
10. `src/Orleans.Streaming/MemoryStreams/MemoryAdapterFactory.cs`
11. `src/Orleans.Streaming/MemoryStreams/MemoryMessageBody.cs`
12. `src/Orleans.Streaming/MemoryStreams/MemoryBatchContainer.cs`
13. `src/Orleans.Streaming/PubSub/PubSubRendezvousGrain.cs`
14. `src/Orleans.Streaming/QueueBalancer/QueueBalancerBase.cs`

---

## 11. 这一篇的落点

把这一篇压成一句话，就是：

Orleans 的 streaming 不是一个“发事件”的功能，而是一套由 runtime、adapter、pub/sub、queue balancer 和 pulling agent 拼出来的投递系统。

它之所以强，是因为它把可靠性、分发、订阅、恢复都收进来了。

它之所以重，也是因为这些东西都不是单独一个类能兜住的。

如果后面继续写，我建议下一篇接：

- `Client / Gateway / callback 链`

这样流、消息、连接、回调就能放到同一张图里看了。

