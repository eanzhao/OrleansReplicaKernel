# 从 GetGrain 到方法返回的一次完整调用链（第二篇）

这篇只做一件事：把一次最普通的 Orleans 调用，从 `GetGrain(...)` 一直追到方法返回，顺着源码走通。

第一篇讲的是总览，这一篇开始进主干。后面不管你是想继续拆代码生成、消息系统，还是准备自己复刻一版运行时，这条链都得先吃透。

先把结论说死：

- `GetGrain` 不发网络请求。
- `GetGrain` 不查目录，也不保证目标 Grain 已经存在。
- `GetGrain` 只是构造了一个强类型代理，本质上是 `GrainReference` 的一个生成子类。
- 真正发请求，是在你调用代理方法的那一刻。
- 真正决定目标 Silo 的，是 `PlacementService` 和 `GrainLocator`。
- 真正创建 Grain 实例的，是目标 Silo 上的 `Catalog`。
- 真正让调用方 `await` 结束的，是响应回来之后 `CallbackData -> ResponseCompletionSource` 这一段。

如果你脑子里先有这一版轮廓，后面的代码就不会看着像一团雾。

---

## 1. 先看完整主链

先压成一条线：

```text
GetGrain
  -> GrainFactory
  -> GrainReferenceActivator
  -> 生成的代理类（本质上仍然是 GrainReference）
  -> 代理方法把参数打包成 IInvokable
  -> GrainReferenceRuntime.InvokeMethodAsync
  -> RuntimeClient.SendRequest
  -> Message / CallbackData
  -> 客户端走 Gateway，Silo 内部调用走 MessageCenter.AddressAndSendMessage
  -> PlacementService + GrainLocator 决定目标 Silo
  -> 目标 Silo MessageCenter.ReceiveMessage
  -> Catalog.GetOrCreateActivation
  -> ActivationData.ReceiveRequest / RunMessageLoop
  -> InsideRuntimeClient.Invoke
  -> GrainMethodInvoker / request.Invoke()
  -> 真正的 Grain 方法
  -> Response
  -> SendResponse
  -> 调用方 RuntimeClient.ReceiveResponse
  -> CallbackData.DoCallback
  -> ResponseCompletionSource.Complete
  -> await 恢复，方法返回
```

这条链里有两个很容易看错的点。

第一，寻址和激活不是一回事。`PlacementService` 解决的是“这条消息该去哪个 Silo”；`Catalog` 解决的是“这个 Silo 上有没有对应 activation，没有就建一个”。

第二，`GetGrain` 和“第一次真正发消息”是分开的。也就是说，拿引用几乎是零副作用的，真正昂贵的动作都延后到了方法调用时。

---

## 2. `GetGrain` 到底返回了什么

入口在：

- `src/Orleans.Core/Core/GrainFactory.cs`

最关键的方法是：

- `GrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix = null)`

这段事其实不多，主要就四步：

1. 把你传进来的 key 转成 `IdSpan`
2. 用 `GrainInterfaceTypeResolver` 把接口类型转成 Orleans 自己的 `GrainInterfaceType`
3. 用 `GrainInterfaceTypeToGrainTypeResolver` 推出 `GrainType`
4. 组装 `GrainId`，再交给 `GrainReferenceActivator.CreateReference(...)`

这里没有网络，没有目录查找，也没有激活。

也就是说，如果你连续写：

```csharp
var a = client.GetGrain<IMyGrain>(1);
var b = client.GetGrain<IMyGrain>(1);
```

你做的事情只是构造了两个指向同一个 `GrainId` 的引用。Orleans 直到你真的调用 `await a.Foo()` 才会开始跑后面的远程调用链。

---

## 3. `GrainReferenceActivator` 在干什么

关键文件：

- `src/Orleans.Core/GrainReferences/GrainReferenceActivator.cs`

这里是创建引用的总入口。它会按 `(GrainType, GrainInterfaceType)` 找合适的 activator，然后构造具体的引用对象。

真正重要的不是“它 new 了一个对象”，而是它决定了这个对象的具体类型。

对强类型 Grain 接口来说，最终拿到的一般不是裸 `GrainReference`，而是代码生成器产出的代理类。`RpcProvider` 会从类型清单里找到这个代理类型，然后用它来构造实例。

所以 `GetGrain<IMyGrain>(...)` 这件事，实际效果更接近：

```text
按 GrainId 构造一个实现了 IMyGrain 的代理对象
```

这个代理对象：

- 对外长得像你的 Grain 接口
- 对内继承 `GrainReference`
- 真正的方法体不是业务代码，而是“打包请求 + 交给运行时”

这里就是 Orleans 强类型 RPC 体验的根。

---

## 4. 代理方法是怎么把调用变成请求的

关键文件：

- `src/Orleans.CodeGenerator/ProxyGenerator.cs`
- `src/Orleans.Core.Abstractions/Runtime/GrainReference.cs`

生成代理这层的核心工作，是把：

```csharp
await grain.DoWork(x, y);
```

变成：

```text
构造一个 IInvokable 请求对象
写入参数
必要时拷贝参数
调用 GrainReference 上的 InvokeAsync / Invoke
```

`GrainReference` 本身不关心你的业务方法名是什么，它只认“请求对象”。请求对象实现的是 `IInvokable`，里面带着：

- 目标接口信息
- 方法信息
- 参数
- 返回值类型信息
- 最终怎么调用真正目标方法的逻辑

也就是说，Orleans 的一次调用，从这一步开始已经不再是普通 C# 方法调用了，而是进入了“生成代码 + 运行时消息”的世界。

---

## 5. `GrainReferenceRuntime`：从代理跳进运行时

关键文件：

- `src/Orleans.Core/Runtime/GrainReferenceRuntime.cs`
- `src/Orleans.Core/Runtime/OutgoingCallInvoker.cs`

代理最后会走到 `GrainReferenceRuntime.InvokeMethodAsync(...)`。

这一步有两个分支：

### 5.1 没有出站过滤器

这是最直的主路径：

1. 给请求里的 `GrainCancellationToken` 补上目标 Grain
2. 从池里拿一个 `ResponseCompletionSource<TResult>`
3. 调 `RuntimeClient.SendRequest(...)`
4. 直接把 `responseCompletionSource.AsValueTask()` 返回给上层

这就是 Orleans 为啥在这条链上分配很克制。它没有一层一层 `TaskCompletionSource<T>` 地 new，而是走了池化的 `ValueTaskSource`。

### 5.2 有出站过滤器

如果注册了 `IOutgoingGrainCallFilter`，或者请求对象自己也参与过滤链，就会走 `OutgoingCallInvoker<TResult>`。

它做的事情很简单：

- 先跑系统级出站过滤器
- 再跑请求级过滤器
- 最后才真正发请求

过滤器链跑完以后，底层仍然是 `sendRequest(reference, callback, request, options)`。也就是说，过滤器只是包一层，不改主干模型。

---

## 6. `RuntimeClient.SendRequest`：这里开始真正造消息

这一段要分成两种情况看。

### 6.1 从外部 Client 发起

关键文件：

- `src/Orleans.Core/Runtime/OutsideRuntimeClient.cs`
- `src/Orleans.Core/Messaging/ClientMessageCenter.cs`

`OutsideRuntimeClient.SendRequest(...)` 会：

1. 用 `MessageFactory.CreateMessage(request, options)` 造 `Message`
2. 填上 `InterfaceType`、`InterfaceVersion`
3. 填上 `SendingGrain = 当前 client 自己的地址`
4. 填上 `TargetGrain = 目标 GrainId`
5. 如果不是 oneway，就创建 `CallbackData`，放进 `callbacks` 字典
6. 把消息交给 `ClientMessageCenter.SendMessage(...)`

这里有个很关键的事实：

外部 Client 自己不做放置决策。它只是挑一个 gateway 连接把消息送进集群。

`ClientMessageCenter` 会按 grain hash 把消息稳定路由到某个 gateway 连接，这样同一个 grain 的消息尽量保持顺序。但“这个 grain 最终该去哪个 Silo”，不是 client 这边算的，而是集群里的 Silo 运行时算的。

### 6.2 从 Grain 或 Silo 内部发起

关键文件：

- `src/Orleans.Runtime/Core/InsideRuntimeClient.cs`
- `src/Orleans.Runtime/Messaging/MessageCenter.cs`

`InsideRuntimeClient.SendRequest(...)` 做的事和外部 client 很像，也是：

- 造 `Message`
- 填发送方、目标方、接口类型、版本
- 非 oneway 调用注册 `CallbackData`

但它和外部 client 有一个关键区别：

它会直接调 `MessageCenter.AddressAndSendMessage(message)`。

也就是说，Silo 内部发起的 Grain-to-Grain 调用，会立刻进入集群内的寻址和放置流程，不需要先过 gateway 那一层。

---

## 7. `PlacementService`：真正决定消息去哪台 Silo

关键文件：

- `src/Orleans.Runtime/Messaging/MessageCenter.cs`
- `src/Orleans.Runtime/Placement/PlacementService.cs`
- `src/Orleans.Runtime/GrainDirectory/GrainLocator.cs`
- `src/Orleans.Runtime/GrainDirectory/CachedGrainLocator.cs`

Silo 侧最关键的入口是：

- `MessageCenter.AddressAndSendMessage(message)`

它内部会调：

- `placementService.AddressMessage(message)`

这一步的逻辑可以概括成三层：

### 7.1 先看消息是不是已经有完整目标地址

如果 `message.IsTargetFullyAddressed`，直接发，不再算。

这通常出现在系统目标或者已经知道确切目标 Silo 的场景。

### 7.2 先查本地缓存

`PlacementService.AddressMessage` 先走：

- `GrainLocator.TryLookupInCache(grainId, out result)`

如果缓存命中，并且缓存没有被这条消息附带的失效信息推翻，那就直接把：

- `message.TargetSilo = result.SiloAddress`

然后发送。

### 7.3 缓存没命中，再走真正的 lookup / placement

这时会进 `PlacementWorker`。

这块实现值得单独记一下，因为它不是“每条消息各算各的”，而是：

- 先按 `grainId.GetUniformHashCode() % 16` 选一个 worker
- 同一个 grain 的并发消息会汇到同一个 work item
- 第一个消息负责触发 lookup / placement
- 后面的消息挂在后面等结果

这意味着 Orleans 在放置这一步做了去重，避免同一个 grain 并发创建一堆 placement 请求。

`PlacementWorker.GetOrPlaceActivationAsync(...)` 的核心逻辑是：

1. 先 `GrainLocator.Lookup(targetGrain)`
2. 如果目录里已经有地址，直接返回那个 Silo
3. 如果没有，按 grain 的 `PlacementStrategy` 找 `PlacementDirector`
4. 调 `director.OnAddActivation(...)` 决定要放到哪台 Silo
5. 更新本地缓存

这里得特别注意一句：

这一步决定的是“消息应该去哪台 Silo”，还不是“activation 已经建好了”。activation 的创建发生在接收端。

---

## 8. 消息到达目标 Silo 后，谁来接

关键文件：

- `src/Orleans.Runtime/Messaging/MessageCenter.cs`
- `src/Orleans.Runtime/Catalog/Catalog.cs`

目标 Silo 收到消息后，会走：

- `MessageCenter.ReceiveMessage(msg)`

它先分两类：

- `Response` 消息：直接交给 `catalog.RuntimeClient.ReceiveResponse(msg)`
- `Request` / `OneWay` 消息：走 `Catalog.GetOrCreateActivation(...)`

`Catalog.GetOrCreateActivation(...)` 这段非常关键，因为它把“目录寻址”和“本地 activation 生命周期”接起来了。

它的逻辑是：

1. 先看本地 activation 目录里有没有现成的 `IGrainContext`
2. 有的话直接返回
3. 没有的话，按 grainId 的 striped lock 进临界区
4. 再查一遍，防止并发重复创建
5. 如果当前 Silo 还是 `Active`，就创建一个新的 `GrainAddress`
6. 调 `grainActivator.CreateInstance(address)` 创建本地 grain context
7. 记录到 activation 目录
8. 调 `result.Activate(requestContextData)` 开始激活
9. 立刻把这个 context 返回

这里最容易忽略的一点是：

`GetOrCreateActivation` 返回时，这个 activation 不一定已经 fully activated。Orleans 的做法是先把 context 建出来，后面的请求先排队，等 activation 进入可处理状态以后再继续跑。

这个思路很实用，也很 Orleans。

---

## 9. `ActivationData`：真正的本地调用中枢

关键文件：

- `src/Orleans.Runtime/Catalog/ActivationData.cs`

如果你只想找一个“Orleans 运行时最核心、同时也最容易长成大泥球”的类，基本就是它。

消息到了 activation 以后，主要走这几步。

### 9.1 `ReceiveRequest`

请求消息进来后：

1. 先检查 overload
2. 没超载就把消息放进 `_waitingRequests`
3. `Signal` 唤醒消息循环

### 9.2 `RunMessageLoop`

这就是 activation 自己的消息泵。源码里甚至直接写明了：这个循环原则上不会退出。

它每轮主要干两件事：

1. 如果当前没有执行中的请求，就先处理内部 operation
2. 然后 `ProcessPendingRequests()`

### 9.3 `ProcessPendingRequests`

这里是 Orleans 单线程 activation 模型真正落地的地方。

它会挨个检查队列里的消息，看当前能不能执行：

- activation 状态是否有效
- 当前是否允许 interleave
- 当前是否允许 reentrant
- 接口版本是否兼容

真正决定“这条消息现在能不能跑”的，是 `MayInvokeRequest(message)`。

如果不能跑，就继续看下一条；如果能跑，就：

1. 从等待队列移除
2. 记到 `_runningRequests`
3. 必要时把它设成 `_blockingRequest`
4. 调 `InvokeIncomingRequest(message)`

也就是说，Orleans 的 activation 内部并不是一个简单的 `ConcurrentQueue + Task.Run`。它有自己的一套排队、择机执行、交错执行、版本兼容和失效处理逻辑。

---

## 10. 真正调用到 Grain 方法，是在 `InsideRuntimeClient.Invoke`

关键文件：

- `src/Orleans.Runtime/Core/InsideRuntimeClient.cs`
- `src/Orleans.Core/Core/GrainMethodInvoker.cs`

`ActivationData.InvokeIncomingRequest` 最后会调：

- `_shared.InternalRuntime.RuntimeClient.Invoke(this, message)`

在 Silo 里，这个 `RuntimeClient` 实际上就是 `InsideRuntimeClient`。

`InsideRuntimeClient.Invoke(...)` 里面最重要的几步是：

1. 如果消息已经超时，直接丢掉
2. 导入 `RequestContext`
3. 从 `message.BodyObject` 里取出 `IInvokable`
4. `invokable.SetTarget(target)`
5. 给请求里的取消 token 注册目标
6. 如果有入站过滤器，就走 `GrainMethodInvoker`
7. 否则直接 `await invokable.Invoke()`
8. 把返回值包装成 `Response`
9. 不是 oneway 的话，发送响应

这里真正落到你的 Grain 方法的，不是 `GrainMethodInvoker` 本身，而是 `IInvokable.Invoke()`。

`GrainMethodInvoker` 更多是包一层入站过滤器：

- 系统级 `IIncomingGrainCallFilter`
- Grain 实例自己实现的 `IIncomingGrainCallFilter`

过滤器都跑完以后，最终还是进 `request.Invoke()`。

所以从设计上看，Orleans 的真正“方法调用单位”不是接口方法，也不是 `MethodInfo`，而是生成出来的 `IInvokable`。

这点对复刻非常重要。

---

## 11. 方法执行完以后，响应是怎么回去的

关键文件：

- `src/Orleans.Runtime/Core/InsideRuntimeClient.cs`
- `src/Orleans.Core/Runtime/OutsideRuntimeClient.cs`
- `src/Orleans.Core/Runtime/CallbackData.cs`
- `src/Orleans.Serialization/Invocation/ResponseCompletionSource.cs`

主线也很清楚：

1. 目标 Grain 方法执行完，拿到 `Response`
2. `InsideRuntimeClient.SafeSendResponse(...)`
3. `MessageCenter.SendResponse(...)`
4. 消息通过网络回到调用方
5. 调用方 `RuntimeClient.ReceiveResponse(...)`
6. 从回调表里把对应的 `CallbackData` 取出来
7. `CallbackData.DoCallback(response)`
8. `IResponseCompletionSource.Complete(response)`
9. `ResponseCompletionSource` 把 `ValueTask` 完成掉
10. 上层 `await` 恢复，方法返回

`CallbackData` 做了三件事：

- 持有原始请求消息和超时信息
- 负责取消、超时、目标 Silo 故障这些异常路径
- 收到响应后把结果写回 completion source

`ResponseCompletionSource` 做的事反而很朴素：

- 内部包了一个 `ManualResetValueTaskSourceCore<T>`
- 完成时写入结果或异常
- `GetResult()` 后重置并归还对象池

这套东西看起来不花哨，但非常贴运行时：少分配、路径短、返回值和异常统一都走 `Response`。

---

## 12. 一次调用里几个常见分叉

前面讲的是最直的一条 happy path。源码里还有几条很常见的分叉，读的时候最好一起记住。

### 12.1 oneway 调用

如果是 oneway：

- 发送端不注册 `CallbackData`
- 接收端执行完也不回响应
- 调用方不会等结果

### 12.2 地址缓存命中

如果 `GrainLocator.TryLookupInCache(...)` 命中：

- 不走目录 lookup
- 不走 placement
- 直接发往缓存里的 Silo

这是正常调用提速的关键路径。

### 12.3 本地没有 activation

如果目标 Silo 上还没有 activation：

- `Catalog.GetOrCreateActivation(...)` 新建 context
- 先开始激活
- 请求先入队
- 激活完成后再继续执行

### 12.4 超时和取消

发送端会有一个定时器扫回调表。

如果超时：

- `CallbackData.OnTimeout()`
- 可能向远端发取消
- 本地先把 promise 打断

如果调用方主动取消：

- `CallbackData.OnCancellation()`
- 是否等待远端确认，取决于配置

### 12.5 拒绝、失效和转发

如果消息打到了过期地址、重复 activation、正在退出的 Silo，或者目标过载：

- 可能收到 rejection
- 可能带 cache invalidation header
- 调用方会失效本地缓存
- 某些情况下消息会被转发到新地址

所以 Orleans 的“调用成功”不是纯 RPC 思维里的“一发一收”，而是一条会和目录缓存、成员变化、activation 生命周期持续互动的链。

---

## 13. 这条链里最值得记住的几个对象

| 对象 | 职责 | 备注 |
| --- | --- | --- |
| `GrainFactory` | 把接口类型和 key 变成 grain reference | 只造引用，不发请求 |
| `GrainReferenceActivator` | 决定返回哪种引用对象 | 强类型场景下通常是生成代理 |
| 生成代理 + `IInvokable` | 把接口方法调用改写成请求对象 | Orleans 强类型 RPC 的核心 |
| `GrainReferenceRuntime` | 把代理调用送进运行时 | 负责 callback source 和出站过滤器 |
| `OutsideRuntimeClient` / `InsideRuntimeClient` | 真正造消息、管回调、收响应 | Client 和 Silo 各有一套 |
| `PlacementService` | 给消息找目标 Silo | 先查 cache，再 lookup，再 placement |
| `GrainLocator` | 屏蔽底层目录实现 | 默认实现里会做 cache |
| `Catalog` | 管本地 activation 的创建和索引 | 本地生命周期入口 |
| `ActivationData` | activation 的消息泵和状态机 | Orleans 运行时真正的中枢 |
| `GrainMethodInvoker` | 入站过滤器管道 | 最终还是落到 `request.Invoke()` |
| `CallbackData` | 调用方侧的 promise 管理器 | 负责超时、取消、回调 |
| `ResponseCompletionSource` | 池化的 `ValueTaskSource` | 低分配返回路径 |

---

## 14. 站在“准备复刻”的角度，最该吸收什么

我觉得这条链里有四个设计判断特别关键。

### 14.1 `GetGrain` 必须是轻的

如果 `GetGrain` 就开始查目录、拉实例、做握手，整套模型就会变笨，而且很难缓存、很难组合、很难让用户放心到处拿引用。

Orleans 这里的判断是对的：引用获取和真实调用解耦。

### 14.2 运行时真正处理的单位应该是“请求对象”，不是反射调用

Orleans 把方法调用提前编译成 `IInvokable`，这一手非常值。

这样做以后：

- 参数打包是静态代码
- 方法分发不是到处反射
- 过滤器、序列化、响应处理都能围绕统一的请求对象展开

如果你准备复刻，我会把这件事当成第一原则。

### 14.3 寻址和激活最好分层

`PlacementService` 解决“消息该去哪台机器”，`Catalog` 解决“本地有没有实例，没有就创建”。这两个职责分开以后，很多复杂度至少还有落脚点。

这是 Orleans 这条链里比较干净的一部分。

### 14.4 `ActivationData` 的能力确实有点太重了

这也是我读到这里更确认的一件事。

`ActivationData` 同时背着：

- mailbox
- 调度
- in-flight request 管理
- overload 检查
- reentrancy / interleaving 判定
- activation 生命周期
- stuck 检测
- forwarding / rejection

这类类一旦继续长，后面会越来越难拆。

如果目标是“完整复刻一版，但架构要更干净”，我会优先考虑把它拆成几块：

- `ActivationMailbox`
- `ActivationScheduler`
- `ActivationStateMachine`
- `InFlightRequestRegistry`

Orleans 现在这版不是不能跑，而是核心机制太多地挤在一个对象里了。

---

## 15. 建议你下一轮重点盯的文件

如果你要顺着这篇继续往下读，我建议按这个顺序回看源码：

1. `src/Orleans.Core/Core/GrainFactory.cs`
2. `src/Orleans.Core/GrainReferences/GrainReferenceActivator.cs`
3. `src/Orleans.CodeGenerator/ProxyGenerator.cs`
4. `src/Orleans.Core.Abstractions/Runtime/GrainReference.cs`
5. `src/Orleans.Core/Runtime/GrainReferenceRuntime.cs`
6. `src/Orleans.Core/Runtime/OutsideRuntimeClient.cs`
7. `src/Orleans.Core/Messaging/ClientMessageCenter.cs`
8. `src/Orleans.Runtime/Messaging/MessageCenter.cs`
9. `src/Orleans.Runtime/Placement/PlacementService.cs`
10. `src/Orleans.Runtime/GrainDirectory/CachedGrainLocator.cs`
11. `src/Orleans.Runtime/Catalog/Catalog.cs`
12. `src/Orleans.Runtime/Catalog/ActivationData.cs`
13. `src/Orleans.Runtime/Core/InsideRuntimeClient.cs`
14. `src/Orleans.Core/Core/GrainMethodInvoker.cs`
15. `src/Orleans.Core/Runtime/CallbackData.cs`
16. `src/Orleans.Serialization/Invocation/ResponseCompletionSource.cs`

如果你只想抓最主干的五个类，那就是：

- `GrainFactory`
- `GrainReferenceRuntime`
- `PlacementService`
- `Catalog`
- `ActivationData`

---

## 16. 一句话收尾

把整条调用链压成一句话，就是：

`GetGrain` 只是在本地拿一个“知道自己是谁、知道该调用哪个接口”的代理；真正的远程调用从代理方法开始，经由 `RuntimeClient -> Message -> Placement -> Catalog -> ActivationData -> IInvokable.Invoke()` 落到目标 Grain，再通过 `CallbackData -> ResponseCompletionSource` 把结果送回调用方。

这条链读顺了，Orleans 的主干就不神秘了。后面无论是继续拆代码生成，还是单独拆 activation/scheduler/placement，都有了坐标。
