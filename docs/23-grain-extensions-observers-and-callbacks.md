# Grain 扩展、Observer 与回调对象：Orleans 为什么要把这几种引用分开做（第二十三篇）

这一篇只讲一件事：Orleans 里除了普通 grain 调用，还有两类很容易混的东西。

一类是 `IGrainExtension`。它看起来像“挂在 grain 身上的附加接口”，实际是运行时给某个 `GrainContext` 动态装上的扩展对象。

另一类是 observer / object reference。它看起来也像“对象引用”，但它传的不是远端 grain 的身份，而是本地对象的回调句柄。客户端和 silo 都能创建它，但语义和普通 grain 引用完全不同。

先把结论说清楚：

- `IGrainExtension` 不是普通接口的另一种写法，它绑定在当前 `IGrainContext` 上，靠 `IGrainExtensionBinder` 管生命周期和缓存。
- 扩展接口的调用链和普通 grain 调用很像，都会走 codegen、`GrainReferenceRuntime`、`IInvokable`，但目标对象不是目录里的 grain，而是当前 activation 或 system target 里的扩展实现。
- observer / object reference 不是 grain reference。它们用 `ObserverGrainId` 这类专用 id，把本地对象映射成一个可回调的地址。
- `CreateObjectReference` 只负责把本地对象注册进 `InvokableObjectManager`，真正发回来的消息还是按 observer id 路由到本地对象。
- 这套设计的代价是，Orleans 里“引用”不是一种东西，而是至少三种：普通 grain 引用、grain 扩展引用、对象回调引用。

如果把这一篇压成一句话，就是：

> Orleans 把“可调用对象”拆成了三种引用语义，能工作得很稳，但边界也确实不够统一。

---

## 1. 先把整条链压成一张图

```text
普通 grain 调用
  -> GetGrain(...)
  -> GrainReference
  -> generated proxy
  -> GrainReferenceRuntime.InvokeMethodAsync
  -> RuntimeClient.SendRequest
  -> 目录 / placement / catalog
  -> grain activation 执行

grain extension 调用
  -> IGrainExtension 接口
  -> codegen 生成 extension invoker / proxy
  -> 扩展方法在当前 GrainContext 上解析
  -> IGrainExtensionBinder.GetExtension / GetOrSetExtension
  -> ActivationData / SystemTarget / HostedClient / ClientGrainContext
  -> 本地 extension 实现被调用

observer / object reference 调用
  -> IGrainFactory.CreateObjectReference(...)
  -> ObserverGrainId
  -> InvokableObjectManager.Register(...)
  -> 返回一个 GrainReference
  -> 调用时消息进入本地对象泵
  -> InvokableObjectManager.Dispatch(...)
  -> LocalObjectData.ReceiveMessage(...)
  -> 本地对象执行回调
```

这张图里最关键的一点是：

普通 grain 调用是在找“远端 activation”。

extension 调用是在找“当前 activation 上挂着的附加实现”。

observer 调用是在找“本地对象的回调入口”。

这三条链长得像，但不是同一条协议。

---

## 2. `IGrainExtension` 到底是什么

关键文件：

- `src/Orleans.Core.Abstractions/Runtime/IGrainExtension.cs`
- `src/Orleans.Core.Abstractions/Core/IGrainContext.cs`
- `src/Orleans.Core.Abstractions/Runtime/GrainContextComponentExtensions.cs`
- `src/Orleans.Runtime/Catalog/ActivationData.cs`
- `src/Orleans.Runtime/Core/SystemTarget.cs`
- `src/Orleans.Runtime/Core/HostedClient.cs`
- `src/Orleans.Core/Runtime/ClientGrainContext.cs`

### 2.1 它不是普通 interface

`IGrainExtension` 只是一个 marker interface：

```csharp
public interface IGrainExtension : IAddressable
```

但它背后挂了一个重要标记：

```csharp
[GenerateMethodSerializers(typeof(GrainReference), isExtension: true)]
```

这说明 Orleans 的 codegen 把 extension 当成一类特殊的可调用接口处理。

`CodeGenerator` 里会读这个 `isExtension` 标记，把 proxy base 分成两种：

- 普通 grain reference
- extension reference

这不是纯命名差异，生成出来的 invokable/proxy 也会带上 `Ext` 区分。

### 2.2 扩展不是挂在类型上，是挂在 `IGrainContext` 上

`IGrainExtensionBinder` 才是真正的挂载入口。它定义了两件事：

- `GetExtension<T>()`
- `GetOrSetExtension<TExtension, TExtensionInterface>(...)`

`IGrainContext`、`ActivationData`、`SystemTarget`、`HostedClient`、`ClientGrainContext` 都实现了这个 binder。

这说明 Orleans 的扩展不是“全局注册一个实现类然后到处取”，而是“某个上下文对象持有自己的 extension 缓存”。

也就是说，extension 的归属单位不是程序集，也不是服务容器，而是当前 grain context。

### 2.3 `GetExtension` 和 `GetOrSetExtension` 分别干什么

`GetExtension<T>()` 走的是“先看当前 context 里有没有，没有就去 DI 里找 keyed service”的路线。

`GetOrSetExtension<TImplementation, TInterface>(...)` 走的是“如果没有，就用工厂造一个，再把它和一个 reference 一起缓存起来”的路线。

`ActivationData` 里这段逻辑很直白：

- 如果当前 component 缓存里已经有 `TExtensionInterface`，直接返回
- 否则先调用 `newExtensionFunc()`
- 再把实现对象 `SetComponent<TExtensionInterface>(implementation)`
- 最后通过 `GrainReference.Cast<TExtensionInterface>()` 造一个 reference 给调用方

这就是 Orleans extension 的本质：

> 运行时给当前 activation 装一个本地对象，再顺手造一个能被外界调用的引用。

### 2.4 extension 调用链为什么能像普通接口一样工作

codegen 这边已经替你做好了大部分事。

`GenerateMethodSerializers(typeof(GrainReference), isExtension: true)` 会让生成器知道：

- 这不是普通 grain interface
- 这是 extension interface
- invokable base 和生成类名都要走 extension 分支

所以调用方写出来还是普通接口调用：

```csharp
await extension.DoSomethingAsync();
```

但实际上它最后会变成：

- 一个 extension reference
- 一组 generated invokable
- 当前 `IGrainContext` 上的 `GetExtension` / `GetOrSetExtension`
- 本地 extension 实现的直接调用

这条链保留了 Orleans 一贯的“表面像普通接口，背后是运行时协议”的风格。

---

## 3. Grain 扩展是怎么绑定上的

关键文件：

- `src/Orleans.Runtime/Hosting/HostingGrainExtensions.cs`
- `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`
- `src/Orleans.Streaming/Providers/SiloStreamProviderRuntime.cs`
- `src/Orleans.Streaming/Providers/ClientStreamingProviderRuntime.cs`
- `src/Orleans.Core/Providers/ClientProviderRuntime.cs`
- `src/Orleans.Runtime/Core/HostedClient.cs`

### 3.1 绑定方式很简单，但很 Orleans

`AddGrainExtension<TExtensionInterface, TExtension>()` 只做一件事：

```csharp
services.AddKeyedTransient<IGrainExtension, TExtension>(typeof(TExtensionInterface))
```

它没有专门搞一套“extension registry”。

它只是把 keyed DI 当成一个安装面，再让 `IGrainExtensionBinder` 去按接口类型取回实现。

所以 extension 的装配方式其实是：

1. 先在容器里注册 keyed service
2. 再在某个 grain context 里按接口类型取
3. 取到后缓存起来
4. 同时生成 reference

### 3.2 `ActivationData`、`SystemTarget`、`HostedClient` 都能做这个事

这三类对象都实现了 `IGrainExtensionBinder`。

区别在于它们各自的扩展域不一样：

- `ActivationData` 管普通 grain activation 的扩展
- `SystemTarget` 管系统目标的扩展
- `HostedClient` 管 silo 内 hosted client 的扩展
- `ClientGrainContext` 管 client 进程里的扩展

这不是统一的“扩展宿主”，而是每类 context 自己维护自己的扩展集合。

### 3.3 扩展对象本身通常也是一种服务对象

比如：

- `CancellationSourcesExtension`
- `AsyncEnumerableGrainExtension`
- `IStreamConsumerExtension`
- `ITransactionManagerExtension`
- `ITransactionalResourceExtension`

这些东西都不是普通 grain。

它们是被装进 context 的本地对象，实现特定能力，然后通过 reference 暴露给外界。

这就是 Orleans 扩展机制真正厉害的地方：

它把“内部能力”和“对外调用”合成了一种模式。

---

## 4. Observer 和 object reference 到底是什么

关键文件：

- `src/Orleans.Core.Abstractions/Core/IGrainFactory.cs`
- `src/Orleans.Core/Runtime/OutsideRuntimeClient.cs`
- `src/Orleans.Runtime/Core/HostedClient.cs`
- `src/Orleans.Core/Runtime/InvokableObjectManager.cs`
- `src/Orleans.Core.Abstractions/IDs/ObserverGrainId.cs`
- `src/Orleans.Core.Abstractions/Core/IGrainObserver.cs`
- `src/Orleans.Core.Abstractions/Core/GrainExtensions.cs`

### 4.1 observer 不是 grain

`IGrainObserver` 只是一个 marker interface：

```csharp
public interface IGrainObserver : IAddressable
```

它表示的是“subscriber side”。

也就是说，observer 语义更接近 pub/sub 回调端，而不是被放置、被目录管理的 grain。

### 4.2 `CreateObjectReference` 做的是“把本地对象包装成可回调地址”

`GrainFactory.CreateObjectReference(...)` 最终会落到 runtime client：

- client 侧走 `OutsideRuntimeClient.CreateObjectReference(...)`
- silo 内 hosted client 走 `HostedClient.CreateObjectReference(...)`

它们做的事都类似：

1. 先检查对象不是现成的 grain reference，也不是 grain class
2. 生成一个 `ObserverGrainId`
3. 把本地对象注册到 `InvokableObjectManager`
4. 返回一个 `GrainReference` 给外界

这个 `GrainReference` 看起来像 grain 引用，实际上只是一个能被路由回本地对象的句柄。

### 4.3 `ObserverGrainId` 让这类 id 和普通 grain id 分家

`ObserverGrainId` 不是普通 `GrainId`，它会检查：

- 必须是 client id
- key 里必须带 `+`

这意味着 observer reference 有自己的 id 域。

它不是“普通 grain 上的一种角色”，而是“专门给本地对象回调留的一类地址”。

### 4.4 `InvokableObjectManager` 才是真正的对象路由器

`InvokableObjectManager` 里维护了一个 `ConcurrentDictionary<ObserverGrainId, LocalObjectData>`。

它负责：

- `TryRegister`
- `TryDeregister`
- `Dispatch(Message message)`

当回调消息回来时：

1. 先从 `TargetGrain` 里解析 `ObserverGrainId`
2. 找到对应的 `LocalObjectData`
3. 再把消息放到本地对象自己的 message pump
4. 最后调用真实对象的方法

这里和 grain 调用最大的不同，是它根本不走 placement / catalog / activation 创建。

它就是本地回调对象的一个地址簿。

### 4.5 object reference 的消息泵是本地的

`LocalObjectData` 里会维护：

- `Queue<Message>`
- `Running` 状态
- `_runningRequests`

它收到消息后，如果对象还活着，就把消息排队，然后用 `TaskScheduler.Default` 跑一个本地泵。

这条链和 grain activation 的消息泵很像，但它没有目录，没有 placement，没有 activation 生命周期。

它只关心一件事：

> 这个本地对象还在不在，回调消息怎么按顺序喂给它。

---

## 5. 为什么 observer / reference 语义和普通 grain 调用不一样

这部分最容易写成一句“它们语义不同”，但源码里真正的差别有四层。

### 5.1 身份不同

普通 grain reference 的身份是：

- `GrainId`
- `GrainInterfaceType`

observer reference 的身份是：

- `ObserverGrainId`
- client-scoped observer id

一个是“远端 activation 的身份”，一个是“本地对象回调的身份”。

### 5.2 路由不同

普通 grain 调用会走：

- `GrainReferenceRuntime`
- `RuntimeClient.SendRequest`
- `PlacementService`
- `Catalog`

observer 调用会走：

- `InvokableObjectManager`
- `LocalObjectData`
- 本地 message pump

这就决定了它们一个要依赖集群 topology，一个只依赖本地注册表。

### 5.3 生命周期不同

普通 grain 引用的生命周期跟 activation 绑定。

observer / object reference 的生命周期跟本地对象绑定：

- 对象 GC 了，引用就得清掉
- 调用进来时如果对象已经没了，就直接 deregister

所以 observer 这一侧的重点不是 activation 回收，而是对象存活检测。

### 5.4 约束不同

`IGrainObserver` 不允许你把普通对象直接当成 observer 传进去。

`GrainReferenceCodecProvider` 和 `TypedGrainReferenceCodec<T>` 都会对 `IGrainObserver` 做显式拦截：

- 不是合法 observer，就抛 `Did you forget to CreateObjectReference?`

这说明 observer 语义不是“任何 addressable 都能混用”的。

它要求对象先被框架正规化，再进入回调链。

---

## 6. 扩展、Observer、普通 Grain Reference 的关系

这三者其实是三种不同的调用语义。

### 6.1 普通 grain reference

它代表远端 activation。

它依赖：

- directory
- placement
- catalog
- runtime message center

它的目标是“把请求送到某个 grain 实例”。

### 6.2 grain extension reference

它代表当前 context 上挂着的附加能力。

它依赖：

- `IGrainExtensionBinder`
- keyed DI
- `ActivationData` / `SystemTarget` / `HostedClient` / `ClientGrainContext`
- codegen 生成的 extension invoker

它的目标是“把请求送到当前 activation 的附加实现”。

### 6.3 observer / object reference

它代表本地对象的回调句柄。

它依赖：

- `ObserverGrainId`
- `InvokableObjectManager`
- 本地 message pump
- GC / deregister 语义

它的目标是“把回调送回本地对象”。

三者长得都像 `await someReference.Method()`，但底层协议并不一样。

---

## 7. 这块里我觉得不够干净的地方

### 7.1 引用类型太多，心智成本高

Orleans 里至少有三种引用语义：

- grain reference
- extension reference
- observer/object reference

它们的 API 表面很像，但不是同一种东西。

这会让新人一开始很容易把“可调用对象”都当成一个概念，实际读源码时就会撞墙。

### 7.2 `IGrainExtensionBinder` 让 context 变得更重

context 本来已经要管生命周期、调度、消息、状态、回收了。

现在又多了一层 extension binder。

这很实用，但也说明 Orleans 的 context 不是一个单纯的上下文对象，而是一个装了很多职责的运行时壳。

### 7.3 observer 和 grain 的边界其实是人为维持的

observer 不是集群里的一等实体，它只是被包装成了一个可回调地址。

所以它既不像 grain 那么“有身份”，又不像纯本地对象那样“只活在进程里”。

这条边界能跑，但确实有点别扭。

### 7.4 extension 的安装和调用路径太依赖约定

扩展接口要能工作，需要同时满足很多约定：

- 接口要有 `GenerateMethodSerializers`
- context 要实现 `IGrainExtensionBinder`
- 实现类要注册成 keyed `IGrainExtension`
- 调用方要走 generated code

任何一环少了，运行时就会抛 `GrainExtensionNotInstalledException` 或相关绑定错误。

这说明 extension 虽然“像普通接口一样用”，但它其实非常依赖框架协议。

---

## 8. 如果你要自己复刻，我会怎么拆

### 8.1 把引用语义显式分层

不要把“reference”当一个统一词。

至少应该拆成：

- remote grain reference
- local extension reference
- local observer reference

这样读代码和排错都会直接很多。

### 8.2 把 extension 安装面和调用面分开

现在 Orleans 的 extension 绑定、缓存、引用创建是缠在一起的。

更清晰的做法是：

- 安装面负责注册和绑定
- 调用面只负责 reference 和 invokable

### 8.3 把 observer 明确成回调宿主对象

observer 这一类对象，最好不要让它的抽象层看起来像 grain。

它本质上是“回调宿主”，不是“分布式实体”。

如果复刻，应该给它一个更明确的命名和单独的路由层。

---

## 9. 推荐阅读顺序

如果你想把这条线顺下来，建议按这个顺序看：

1. `src/Orleans.Core.Abstractions/Runtime/IGrainExtension.cs`
2. `src/Orleans.Core.Abstractions/Core/IGrainContext.cs`
3. `src/Orleans.Core.Abstractions/Runtime/GrainContextComponentExtensions.cs`
4. `src/Orleans.Runtime/Catalog/ActivationData.cs`
5. `src/Orleans.Runtime/Core/SystemTarget.cs`
6. `src/Orleans.Runtime/Core/HostedClient.cs`
7. `src/Orleans.Core/Runtime/ClientGrainContext.cs`
8. `src/Orleans.Runtime/Hosting/HostingGrainExtensions.cs`
9. `src/Orleans.Core.Abstractions/IDs/ObserverGrainId.cs`
10. `src/Orleans.Core/Core/GrainFactory.cs`
11. `src/Orleans.Core/Runtime/InvokableObjectManager.cs`
12. `src/Orleans.Core/Runtime/OutsideRuntimeClient.cs`
13. `src/Orleans.Core/ClientObservers/ClientObserver.cs`
14. `src/Orleans.Core/ClientObservers/ClientGatewayObserver.cs`
15. `src/Orleans.Core.Abstractions/Core/GrainExtensions.cs`

---

## 10. 这一篇的落点

这一篇其实是在说一件很 Orleans 的事：

它并不把“对象能不能被调用”看成一个统一问题。

它把它拆成了三种场景：

- 远端 grain：走目录和 placement
- 本地扩展：走 context binder 和 keyed DI
- 本地回调对象：走 observer id 和本地对象泵

这三种东西都能 `await`，都像“调用一个接口”，但它们背后的运行时协议完全不同。

也正因为这样，Orleans 才会显得强，但不够整齐。

