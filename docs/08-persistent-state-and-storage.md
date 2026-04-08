# 持久化状态与 Storage Provider：`IPersistentState<T>`、`PersistentState<T>`、`StateStorageBridge` 是怎么串起来的（第八篇）

这一篇专门讲 Orleans 的持久化状态。

它和前面几篇不一样，重点不在“消息怎么跑”或者“序列化怎么拼”，而在“grain 的状态到底是谁在持有、谁在初始化、谁在写回、谁在做并发校验”。

先把结论说直白一点：

- `IPersistentState<T>` 不是一个单纯的状态容器，它其实是一层带 `ReadStateAsync` / `WriteStateAsync` / `ClearStateAsync` 能力的状态外壳。
- `PersistentState<T>` 是这层外壳的默认运行时实现。
- `StateStorageBridge<T>` 才是真正干活的桥，里面包着状态对象、`ETag`、`RecordExists`，也负责把调用转给 `IGrainStorage`。
- `IGrainStorage` 是 storage provider 的统一入口，具体怎么存、怎么取、怎么做版本检查，交给 Azure、Redis、Memory、AdoNet 这些 provider 自己实现。
- `[PersistentState(...)]` 只是构造参数上的一个绑定标记，它把“这个状态该叫什么、用哪个 provider”交给 factory。

如果你把这一层看成“grain 内部自带一个小型状态管理器”，就比较容易读下去了。

---

## 1. 先把整条链压成一张图

```text
构造 grain
  -> [PersistentState(...)] 标记 constructor 参数
  -> PersistentStateAttributeMapper
  -> IPersistentStateFactory
  -> PersistentState<T>
  -> StateStorageBridge<T>
  -> IGrainStorage
  -> 具体 storage provider

激活时
  -> Grain<T>.LifecycleObserver.OnStart
  -> StateStorageBridge.ReadStateAsync
  -> provider.ReadStateAsync(...)
  -> State / ETag / RecordExists 被填充
  -> OnActivateAsync 才能开始用状态

运行中
  -> grain 修改 State
  -> 调用 WriteStateAsync / ClearStateAsync
  -> StateStorageBridge 把 State + ETag 交给 provider
  -> provider 做 optimistic concurrency 检查

停机或迁移时
  -> OnDehydrate / OnRehydrate
  -> StateStorageBridge 参与迁移上下文
  -> 状态对象尽量跟着 activation 一起走
```

这张图里最重要的一点是：

`IPersistentState<T>` 不是自己直接去存储后端读写的，真正做事的是 `StateStorageBridge<T>`，而 `PersistentState<T>` 只是把它挂进生命周期和迁移流程里。

---

## 2. `IPersistentState<T>` 到底是什么

关键文件：

- `src/Orleans.Runtime/Facet/Persistent/IPersistentState.cs`
- `src/Orleans.Runtime/Facet/Persistent/IPersistentStateConfiguration.cs`
- `src/Orleans.Runtime/Facet/Persistent/PersistentStateAttribute.cs`
- `src/Orleans.Runtime/Facet/Persistent/PersistentStateAttributeMapper.cs`

`IPersistentState<T>` 本身很薄，它直接继承了 `IStorage<T>`。

`IStorage<T>` 提供的核心能力是：

- `State`
- `ETag`
- `RecordExists`
- `ReadStateAsync()`
- `WriteStateAsync()`
- `ClearStateAsync()`

也就是说，`IPersistentState<T>` 的语义不是“一个普通 POCO 状态对象”，而是“一个状态对象外加一套持久化操作”。

这一点和 `IGrainState<T>` 很像，但层次不同：

- `IGrainState<T>` 是 provider 和底层存储之间用的状态封装
- `IPersistentState<T>` 是 grain 代码里注入出来的状态管理接口

它们都带 `ETag`，都带 `RecordExists`，但用途不一样。

### 2.1 `[PersistentState(...)]` 是怎么绑上去的

Orleans 不是靠构造函数名字去猜状态名，而是靠参数上的属性：

```csharp
public MyGrain([PersistentState("mystate", "mypersist")] IPersistentState<MyState> state)
```

这里的属性会被 `PersistentStateAttributeMapper` 识别。

它会做三件事：

1. 检查参数类型是不是 `IPersistentState<T>`
2. 读取 `StateName` 和 `StorageName`
3. 把这两个值交给 `IPersistentStateFactory.Create(...)`

如果你没显式填 `stateName`，它还会用参数名当默认 state 名。

这就把“代码里声明的状态”和“运行时到底拿哪一个 storage provider”拆开了。

### 2.2 `IPersistentStateConfiguration` 很朴素

它只有两个字段：

- `StateName`
- `StorageName`

这其实已经说明 Orleans 这里的设计态度了：

它不把 persistent state 看成复杂领域对象，只把它当成“一个状态名 + 一个 provider 名 + 一个泛型状态类型”。

---

## 3. `PersistentState<T>` 和 `StateStorageBridge<T>` 的关系

关键文件：

- `src/Orleans.Runtime/Facet/Persistent/PersistentStateStorageFactory.cs`
- `src/Orleans.Runtime/Storage/StateStorageBridge.cs`

`PersistentState<T>` 是 `StateStorageBridge<T>` 的一个薄封装。

它继承：

- `StateStorageBridge<TState>`

同时实现：

- `IPersistentState<TState>`
- `ILifecycleObserver`

也就是说，真正的状态读写能力在 bridge 里，`PersistentState<T>` 只是把这个 bridge 接进 grain 生命周期。

### 3.1 `PersistentStateFactory` 做了什么

`PersistentStateFactory.Create<TState>(...)` 会先找 storage provider：

- 如果 `StorageName` 不为空，就用 keyed `IGrainStorage`
- 否则就取默认 `IGrainStorage`

找不到就直接抛 `BadProviderConfigException`。

拿到 provider 以后，它会构造：

- `new PersistentState<TState>(fullStateName, context, storageProvider)`

这里 `fullStateName` 目前默认就是 `cfg.StateName`。

所以 `PersistentStateFactory` 本质上只负责两件事：

- 选 provider
- 选 state 名

### 3.2 `PersistentState<T>` 为什么要单独挂生命周期

`PersistentState<T>` 在构造函数里会把自己订阅到 grain lifecycle 的 `SetupState` 阶段：

- `lifecycle.Subscribe(..., GrainLifecycleStage.SetupState, this)`
- `lifecycle.AddMigrationParticipant(this)`

它的 `OnStart(...)` 会在初始化阶段自动读状态，但前提是：

- 没有取消
- 状态还没通过 rehydration 预先装好

如果已经 rehydrate 过了，它就不再重复读一次。

这个判断挺关键。

它说明 Orleans 对“重新激活时状态从哪来”是有优先级的：

1. 能从迁移上下文恢复，就先恢复
2. 不行再去外部 storage 读

这避免了重复 IO，也避免了迁移后的状态被旧存储覆盖。

---

## 4. `StateStorageBridge<T>` 才是实际干活的地方

关键文件：

- `src/Orleans.Runtime/Storage/StateStorageBridge.cs`

`StateStorageBridge<TState>` 是这篇最核心的文件。

它的职责很清楚：

- 持有 `State`
- 持有 `ETag`
- 持有 `RecordExists`
- 持有真正的 `IGrainStorage`
- 把读写清楚地转发给 storage provider
- 处理迁移时的 dehydration / rehydration

### 4.1 它内部包了一层 `GrainState<T>`

`StateStorageBridge<T>` 并不是直接拿 `TState` 做储存，而是内部包了一层：

- `GrainState<TState>`

这个包装层里有：

- `State`
- `ETag`
- `RecordExists`

所以 `StateStorageBridge<T>` 既是状态入口，也是元数据入口。

### 4.2 它不是直接 new provider，而是共享一个 `StateStorageBridgeShared<T>`

`StateStorageBridge` 的另一个容易忽略的点是：

它自己不直接保存一堆重复的共享信息，而是通过 `StateStorageBridgeSharedMap` 拿一个共享对象。

这个共享对象里放的是：

- `Name`
- `ProviderTypeName`
- `StateTypeName`
- `Store`
- `Logger`
- `Activator<TState>`
- `MigrationContextKey`

这样做的效果是：

- 同一个 `(state name, storage provider, state type)` 组合不会重复构建太多元数据
- `Activator<TState>` 和 logger 也只要共享一份

这部分算是 Orleans 里比较实用的一层缓存。

### 4.3 读状态时发生了什么

`ReadStateAsync()` 的流程很直接：

1. 校验当前线程是不是 Orleans 的 activation/runtime 上下文
2. 记录 tracing activity
3. 调 `_shared.Store.ReadStateAsync(...)`
4. 成功后把 `IsStateInitialized = true`
5. 失败就走 `OnError(...)`

这里有个细节：

`StateStorageBridge` 读完后不会只更新 `State`，还会让 `ETag` 和 `RecordExists` 一起跟着 provider 返回值变化。

也就是说，状态对象不是纯数据对象，它还有一套“这个记录是否存在、这版是谁写的”元信息。

### 4.4 写状态和清状态都走同一条错误处理逻辑

`WriteStateAsync()` 和 `ClearStateAsync()` 的结构很像：

- 先校验 runtime context
- 再记录 activity
- 再调用 provider
- 失败就交给 `OnError(...)`

`OnError(...)` 做的事比较狠：

1. 如果 provider 能解 REST 错误，就顺手拿一下更细的错误码
2. 先打日志
3. 如果异常不是 `OrleansException`，就包一层 `OrleansException`
4. 如果本来就是 Orleans 自己的异常，就直接抛

这意味着：

- 底层 provider 可以抛自己的异常
- 但 Orleans 会把非框架异常统一包成 Orleans 风格

这样上层业务代码接收到的错误更统一，但代价是异常链有点厚。

### 4.5 迁移时状态是跟着走的

`StateStorageBridge` 还实现了 `IGrainMigrationParticipant`。

`OnDehydrate(...)` 会把当前 `GrainState<T>` 塞进迁移上下文。

`OnRehydrate(...)` 会从上下文里把这个状态取回来，并把 `IsStateInitialized = true`。

这就解释了为什么 Orleans 的状态对象不只是“落盘用”，还是“迁移用”的。

在它的语义里，持久化状态也是 activation 状态的一部分。

---

## 5. grain 基类里有两条不同的状态入口

关键文件：

- `src/Orleans.Core.Abstractions/Core/Grain.cs`
- `src/Orleans.Core/Providers/GrainStorageHelpers.cs`
- `src/Orleans.Runtime/Core/GrainRuntime.cs`

这里要分清两类 grain：

### 5.1 `Grain<TState>` 是老的、直接内建状态模型

`Grain<TState>` 里有一个私有字段：

- `_storage : IStorage<TState>`

它提供的 protected API 是：

- `State`
- `ReadStateAsync()`
- `WriteStateAsync()`
- `ClearStateAsync()`

这个路径里，`Grain<TState>` 会在 lifecycle 里自己搞一套 `LifecycleObserver`。

它的 `OnStart(...)` 会先看：

- `_isInitialized`
- `_grain._storage?.Etag`

如果已经通过 rehydration 装过状态，就不再重复读。

然后它会调用：

- `_grain.Runtime.GetStorage<TGrainState>(_grain.GrainContext)`

也就是说，`Grain<TState>` 这条老路径是 runtime 直接给它一个 `StateStorageBridge<TState>`。

### 5.2 `IPersistentState<T>` 是更显式的注入模型

而 `IPersistentState<T>` 是通过 constructor parameter 注入进 grain 的。

它不是 runtime 暗塞进去的字段，而是你在 grain 构造函数里明确声明：

```csharp
public MyGrain([PersistentState("stateName")] IPersistentState<MyState> state)
```

这条路径更清楚，状态名和 provider 名也更显式。

### 5.3 `GrainStorageHelpers.GetGrainStorage(...)` 只管 `Grain<T>`

`GrainStorageHelpers` 读取的是 grain 类型上的 `[StorageProvider(...)]`。

它的逻辑是：

- grain 类上有 `StorageProviderAttribute`，就取那个 keyed provider
- 没有就取默认 `IGrainStorage`

这条入口主要服务的是 `Grain<T>` 那条老模型，不是 `IPersistentState<T>` 注入模型。

所以这里能看出 Orleans 其实同时维护了两套状态访问方式：

- 旧式：`Grain<T>`
- 新式：`IPersistentState<T>`

功能上很接近，但装配点和生命周期挂点不完全一样。

---

## 6. storage provider 接口本身长什么样

关键文件：

- `src/Orleans.Core/Providers/IGrainStorage.cs`
- `src/Orleans.Core/Providers/IGrainStorageSerializer.cs`
- `src/Orleans.Core/Providers/StorageSerializer/GrainStorageSerializer.cs`
- `src/Orleans.Runtime/Hosting/StorageProviderHostExtensions.cs`

`IGrainStorage` 的公共入口很简单，只有三件事：

- `ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)`
- `WriteStateAsync<T>(...)`
- `ClearStateAsync<T>(...)`

这里有个值得记的点：

`IGrainStorage` 操作的不是 `IPersistentState<T>`，而是 `IGrainState<T>`。

也就是说：

- `IPersistentState<T>` 是 grain 侧概念
- `IGrainState<T>` 是 provider 侧概念

它们通过 `StateStorageBridge<T>` 连接起来。

### 6.1 provider 的默认装配方式

`StorageProviderExtensions.AddGrainStorage<T>(...)` 会把 provider 注册成 keyed singleton 的 `IGrainStorage`。

如果名字正好是默认 storage provider 名，它还会顺手注册一个默认 `IGrainStorage`。

如果 provider 实现了 `ILifecycleParticipant<ISiloLifecycle>`，它还会被挂进 silo lifecycle。

这说明 storage provider 不只是一个“能读写数据”的服务，它还可以参与 silo 生命周期初始化，比如建立连接、预热客户端、关闭资源。

### 6.2 provider 的序列化器是可替换的

`IStorageProviderSerializerOptions` 里有一个关键字段：

- `GrainStorageSerializer`

`DefaultStorageProviderSerializerOptionsConfigurator<TOptions>` 会在 provider 的 options 里补这个字段。

它的策略是：

1. 优先找同名的 keyed `IGrainStorageSerializer`
2. 找不到就用全局默认 serializer

所以真正序列化 grain state 的东西，通常不是 provider 自己，而是 `IGrainStorageSerializer`。

`GrainStorageSerializer` 这个封装还支持 fallback deserializer，读的时候能容错两个实现。

这块说明 Orleans 把“存储介质”和“序列化格式”分得还是挺清楚的。

---

## 7. `ETag` 和并发控制是怎么做的

关键文件：

- `src/Orleans.Core.Abstractions/Core/IStorage.cs`
- `src/Orleans.Core/CodeGeneration/IGrainState.cs`
- `src/Orleans.Persistence.Memory/Storage/MemoryStorage.cs`
- `src/Redis/Orleans.Persistence.Redis/Storage/RedisGrainStorage.cs`
- `src/Azure/Orleans.Persistence.AzureStorage/Providers/Storage/AzureBlobStorage.cs`

`ETag` 在这里就是乐观并发控制。

最基础的语义在 `IStorage` 注释里写得很清楚：

- 如果 `ETag` 不匹配，写和清都会失败
- `Etag = null` 表示“总是写”或者“总是删”的那种无条件行为，具体还是看 provider 的实现

`IGrainState<T>` 里也把 `ETag` 和 `RecordExists` 一起放在了状态对象上。

这意味着 Orleans 的状态对象不是单纯 payload，而是：

- `State`
- `ETag`
- `RecordExists`

三者绑在一起。

### 7.1 读的时候

provider 读状态时，一般会：

- 如果找到了数据，设置 `State`
- 把 `ETag` 带回来
- `RecordExists = true`

如果没找到，通常会：

- `State = new T()`
- `ETag = null`
- `RecordExists = false`

### 7.2 写的时候

写的时候 provider 一般会先比对 `ETag`。

如果匹配：

- 写成功
- 生成新的 `ETag`
- 回填到 `grainState.ETag`

如果不匹配：

- 抛 `InconsistentStateException`
- 或者 provider 自己的 etag mismatch 异常，再被包装成 Orleans 统一异常

### 7.3 清的时候

`ClearStateAsync` 也会比对 `ETag`。

有些 provider 会直接删除记录，有些 provider 会保留一个空记录，取决于 `DeleteStateOnClear` 之类的配置。

Redis、Azure、Memory 这几个实现都能看到这个思路：

- 乐观并发控制是第一位
- 清除语义可以有 provider 差异

### 7.4 `Memory`、`Redis`、`Azure` 的差别

这几个 provider 的风格不一样，但结论很一致：

- `Memory` 用自己内部的 `MemoryStorageGrain` 来模拟 etag 检查
- `Redis` 用 Lua script 原子比较并更新
- `Azure Blob/Table` 用云存储自带的条件写入机制

所以 Orleans 把并发控制要求统一了，但不强迫后端实现同一种技术。

这也是它比较现实的一点。

---

## 8. 异常语义不要混

关键文件：

- `src/Orleans.Core/Providers/IGrainStorage.cs`
- `src/Orleans.Runtime/Storage/StateStorageBridge.cs`
- `src/Orleans.Persistence.Memory/Storage/MemoryStorage.cs`
- `src/Redis/Orleans.Persistence.Redis/Storage/RedisGrainStorage.cs`
- `src/Azure/Orleans.Persistence.AzureStorage/Providers/Storage/AzureBlobStorage.cs`

这里的异常大概分三层。

### 8.1 业务层异常

最典型的是：

- `InconsistentStateException`

它表示并发冲突，不是网络坏了，也不是序列化坏了。

### 8.2 provider 自己的异常

比如：

- RedisStorageException
- Azure Blob / Table 的请求失败异常
- MemoryStorageEtagMismatchException

这些是介质层异常。

### 8.3 Orleans 包装后的异常

`StateStorageBridge` 在接住 provider 异常以后，会尽量把它统一成 Orleans 风格的异常。

如果本来就是 OrleansException，就直接抛；不是的话就包一层。

这会让上层 grain 代码看到的错误比较统一，但也有一个副作用：

堆栈和根因信息会更长，排查时得顺着 inner exception 往下找。

---

## 9. 状态对象和 provider 的关系，别看反了

这一段是这篇最容易讲乱的地方。

### 9.1 grain 里拿到的是状态管理对象，不是 storage provider

当你在 grain 构造函数里注入 `IPersistentState<T>` 时，你拿到的不是 Azure、Redis、Memory 这种具体 provider。

你拿到的是一个管理器式对象，它内部已经绑好了：

- state name
- storage provider
- `ETag`
- `RecordExists`
- 生命周期挂钩

### 9.2 provider 只认 `IGrainState<T>`

真正进入存储层时，provider 处理的是：

- `grainType`
- `grainId`
- `IGrainState<T>`

它不关心你在 grain 里是用 `IPersistentState<T>` 还是 `Grain<T>`。

### 9.3 `StateStorageBridge` 是中间翻译层

所以这条链真正的中间点是 `StateStorageBridge<T>`。

它把 grain 侧的状态管理语义，翻译成 provider 侧的读写语义。

如果没有这层，`IPersistentState<T>` 就只能直接依赖某个具体 provider，框架也就没法做统一生命周期、迁移和错误处理了。

---

## 10. 这块里我觉得不够干净的地方

既然目标是复刻，问题也要说清楚。

### 10.1 状态入口有两套

`Grain<T>` 一套，`IPersistentState<T>` 一套。

功能上接近，但绑定方式、生命周期、初始化入口都不一样。这个对使用者来说不一定难，但对框架内核来说，重复度是存在的。

### 10.2 `StateStorageBridge` 责任偏多

它同时做了：

- 状态包裹
- `ETag` 管理
- provider 转发
- 生命周期跟踪
- tracing
- migration participation
- 异常包装

这已经不是一个纯桥，而是一个“小型状态运行时”。

### 10.3 provider 的配置和 serializer 的配置是两层

storage provider 选哪个，是一层。

provider 用什么 serializer，又是另一层。

这很灵活，但装配路径也更绕，第一次看源码不太容易一口气串起来。

### 10.4 异常包装比较厚

`StateStorageBridge` 会把很多底层异常包成 OrleansException。

这对框架边界是友好的，但对调试初期来说不够轻。

---

## 11. 如果你要完整复刻，我会怎么拆

如果我们自己重写一版，我会把这块拆成下面几层。

### 11.1 状态声明层

只负责：

- 状态名
- provider 名
- 状态类型
- 注入点绑定

### 11.2 状态桥层

只负责：

- `State`
- `ETag`
- `RecordExists`
- Read / Write / Clear
- 生命周期接入

### 11.3 provider 层

只负责：

- 持久化介质
- optimistic concurrency
- 序列化 / 反序列化

### 11.4 迁移层

只负责：

- dehydrate / rehydrate
- activation 状态搬运

把这四层拆清以后，代码会比 Orleans 现在这版更直观。

---

## 12. 推荐阅读顺序

如果想顺着把这条链吃透，我建议按下面的顺序看：

1. `src/Orleans.Core.Abstractions/Core/IStorage.cs`
2. `src/Orleans.Core/CodeGeneration/IGrainState.cs`
3. `src/Orleans.Runtime/Facet/Persistent/IPersistentState.cs`
4. `src/Orleans.Runtime/Facet/Persistent/IPersistentStateConfiguration.cs`
5. `src/Orleans.Runtime/Facet/Persistent/PersistentStateAttribute.cs`
6. `src/Orleans.Runtime/Facet/Persistent/PersistentStateAttributeMapper.cs`
7. `src/Orleans.Runtime/Facet/Persistent/PersistentStateStorageFactory.cs`
8. `src/Orleans.Runtime/Storage/StateStorageBridge.cs`
9. `src/Orleans.Core/Providers/GrainStorageHelpers.cs`
10. `src/Orleans.Core/Providers/IGrainStorage.cs`
11. `src/Orleans.Core/Providers/IGrainStorageSerializer.cs`
12. `src/Orleans.Core/Providers/StorageSerializer/GrainStorageSerializer.cs`
13. `src/Orleans.Runtime/Core/GrainRuntime.cs`
14. `src/Orleans.Core.Abstractions/Core/Grain.cs`
15. `src/Orleans.Runtime/Hosting/StorageProviderHostExtensions.cs`
16. `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`

把这 16 个文件顺下来，基本就能把 Orleans 的持久化状态系统在脑子里搭起来。

---

## 13. 这篇的落点

把这一篇压成一句话，就是：

Orleans 的持久化状态不是“grain 里一个普通字段”，而是“`IPersistentState<T>` 注入进来以后，通过 `StateStorageBridge<T>` 接上 `IGrainStorage`，再由具体 provider 落到后端存储”的一条完整链。

这条链把三件事绑得很紧：

- grain 生命周期
- 乐观并发控制
- 后端存储实现

它能跑起来，靠的是 Orleans 先把这些边界都写出来了；它看着不够干净，也正是因为这几个边界本来就不是一层就能说清的。
