# Client、Gateway 与响应回调链：OutsideRuntimeClient、ClientMessageCenter、连接管理是怎么串起来的（第十一篇）

这一篇专门讲 Orleans client 侧怎么把一条请求送进集群，又怎么把响应、拒绝、超时、断连这些情况收回来。

这块如果不看源码，很容易把它想成“客户端连上一个 gateway 然后发消息”这么简单。实际上没那么直。

先把结论说清楚：

- `OutsideRuntimeClient` 是 client 侧运行时总入口，负责组装 request、注册 callback、接收 response、处理超时和断连。
- `ClientMessageCenter` 负责“这条消息到底走哪条 gateway 连接”，还要处理重试、拒绝和 bucket 级别的有序路由。
- `GatewayManager` 负责维护可用 gateway 列表，标记 dead/masked gateway，并按刷新周期更新快照。
- `ConnectionManager` 负责真正的连接对象生命周期，包含创建、复用、关闭、失败后重试。
- `CallbackData` 是回调状态机，负责把 response、rejection、timeout、target silo fail 变成最终的 `ResponseCompletionSource` 完成。
- `ResponseCompletionSource` 只是一个很薄的 `ValueTask` 完成器，真正的语义都在上层 callback 里。

如果把这条链压成一句话，就是：

> 请求不是“发出去就完了”，而是先挂一个 callback，再由 gateway 连接把响应带回来；如果响应没回来，就由 timeout、rejection、断连这些路径把这个 callback 终结掉。

---

## 1. 先把整条链压成一张图

```text
GetGrain / 代理方法
  -> GrainReferenceRuntime.InvokeMethodAsync
  -> OutsideRuntimeClient.SendRequest(...)
  -> MessageFactory.CreateMessage(...)
  -> callbacks.TryAdd(message.Id, CallbackData)
  -> ClientMessageCenter.SendMessage(message)
  -> GetGatewayConnection(message)
       -> bucket / round-robin / direct gateway
       -> GatewayManager.GetLiveGateway(s)
       -> ConnectionManager.GetConnection(...)
  -> ClientOutboundConnection.Send(...)
  -> 目标 gateway / silo
  -> 响应回来
  -> ClientOutboundConnection.OnReceivedMessage(...)
  -> ClientMessageCenter.DispatchLocalMessage(...)
  -> OutsideRuntimeClient.ReceiveResponse(...)
  -> callbacks.TryRemove(response.Id)
  -> CallbackData.DoCallback(...)
  -> ResponseCompletionSource.Complete(...)
  -> await 恢复

失败分支
  -> send 失败 / gateway 死亡 / 连接断开
  -> GatewayManager.MarkAsDead(...)
  -> ClientMessageCenter.RejectMessage(...)
  -> CallbackData.OnTimeout / OnTargetSiloFail / OnCancellation
  -> ResponseCompletionSource.Complete(exception)
```

这张图里最重要的一点是：

client 侧不是“发出一条消息就等响应”这么朴素，而是一个有状态的回调系统。消息 id、target silo、bucket、gateway 连接、callback 字典，这些东西是绑在一起的。

---

## 2. `OutsideRuntimeClient` 先做了什么

关键文件：

- `src/Orleans.Core/Runtime/OutsideRuntimeClient.cs`
- `src/Orleans.Core/Runtime/GrainReferenceRuntime.cs`
- `src/Orleans.Serialization/Invocation/ResponseCompletionSource.cs`

### 2.1 请求一进来，先被包装成 `Message`

`GrainReferenceRuntime.InvokeMethodAsync(...)` 最后会调用：

```csharp
RuntimeClient.SendRequest(reference, request, responseCompletionSource, options)
```

在 client 场景下，这个 `RuntimeClient` 就是 `OutsideRuntimeClient`。

`OutsideRuntimeClient.SendRequest(...)` 做的第一件事是：

1. 检查请求上的 cancellation token
2. 调 `MessageFactory.CreateMessage(request, options)`
3. 给消息填上 `InterfaceType`、`InterfaceVersion`
4. 给消息填上 `SendingGrain`、`TargetGrain`
5. 需要的话给系统 target 填 `TargetSilo`
6. 如果不是 one-way，就注册 callback
7. 最后交给 `MessageCenter.SendMessage(...)`

这里要注意，`OutsideRuntimeClient` 自己并不决定连接怎么选。它先把消息对象和 callback 建好，再把送信这件事交给 `ClientMessageCenter`。

### 2.2 callback 不是附属品，是主角

如果不是 one-way：

```csharp
var callbackData = new CallbackData(this.sharedCallbackData, context, message, _applicationRequestInstruments);
callbackData.SubscribeForCancellation(cancellationToken);
callbacks.TryAdd(message.Id, callbackData);
```

这几行非常关键。

`callbacks` 字典的 key 是 `CorrelationId`，也就是消息 id。响应回来以后，client 侧就是靠这个 id 把 response 找回原来的请求。

换句话说：

- `message.Id` 是这个请求的身份证
- `CallbackData` 是这个请求的生命周期管理器
- `ResponseCompletionSource` 是最终把结果交回 `await` 的那个完成器

这三层不是重复，而是分工。

### 2.3 `ResponseCompletionSource` 很薄，薄到几乎只剩壳

`ResponseCompletionSource<TResult>` 本质上就是一个 `ManualResetValueTaskSourceCore<TResult>` 的封装。

它只负责：

- 挂起一个 `ValueTask`
- 接收 `Complete(...)`
- 把结果或者异常送回 `await`
- 用完以后回收到池里

所以真正的业务语义不在它这里，而在 `CallbackData`。

这个分层挺符合 Orleans 的风格：

- “会不会完成”由 callback 管
- “怎么 await”由 `ResponseCompletionSource` 管
- “哪条消息对应哪个请求”由字典管

---

## 3. `ClientMessageCenter` 负责哪一步

关键文件：

- `src/Orleans.Core/Messaging/ClientMessageCenter.cs`
- `src/Orleans.Core/Messaging/GatewayManager.cs`
- `src/Orleans.Core/Networking/ConnectionManager.cs`
- `src/Orleans.Core/Networking/ClientOutboundConnection.cs`

`ClientMessageCenter` 是 client 侧的消息路由器。

它负责的不是业务调用，而是：

> 这条消息应该走哪个 gateway 连接，连接坏了怎么办，消息被拒绝了怎么办，有序消息要不要走同一条 bucket。

### 3.1 它先按消息类型分流

`SendMessage(...)` 里先看 `msg.TargetSilo`、`msg.TargetGrain`、`msg.IsUnordered`。

大概分三种：

1. 明确指定目标 gateway 的消息
2. system target 或 unordered 消息
3. 普通 grain 请求

这三种走法不一样。

### 3.2 明确指定 gateway 的消息，直接去那条连接

如果消息里已经有 `TargetSilo`，而且这个 gateway 还是可用的，就直接走那条连接：

```csharp
var connectionTask = this.connectionManager.GetConnection(siloAddress);
```

这是最短路径。

如果这条连接拿不到，`ConnectAsync(...)` 会在 direct gateway message 分支里把这条消息直接 reject 掉。

这里没有什么“换一个 gateway 再试试”的温柔逻辑。目标 gateway 明明已经指定了，就不会替你改目标。

### 3.3 system target / unordered 消息走 round robin

如果是 system target 或者 unordered，`ClientMessageCenter` 会从 `GatewayManager.GetLiveGateways()` 里拿一个 live gateway，按 round robin 轮一下。

这条路径的意思很简单：

- 这类消息没有强顺序约束，或者本来就不是普通 grain 请求
- 所以没必要绑死在某一个 bucket
- 直接从当前可用 gateway 里挑一个就行

### 3.4 普通 grain 请求走 bucket 路由

这是 Orleans client 侧最有意思的一段。

普通请求不是简单 round robin，而是按 `TargetGrain.GetHashCode()` 做 bucket：

```csharp
var index = GetHashCodeModulo(msg.TargetGrain.GetHashCode(), (uint)grainBuckets.Length);
```

然后每个 bucket 维护一个 `WeakReference<ClientOutboundConnection>`。

这意味着：

- 同一个 grain 的请求，尽量走同一条连接
- 同一 bucket 上的连接还能复用
- 如果连接失效，bucket 会被清掉，下次再重新挑 gateway

这里能看出来 Orleans 很在意“同一个 grain 的顺序”这件事，至少在 client 路由层，它是显式照顾的。

### 3.5 连接拿不到时，消息会被 reject 或重试

`ClientMessageCenter` 里有个很直接的 `RejectMessage(...)`：

- 如果当前 message 不是 request，就直接丢掉
- 如果是 request，就造一个 `RejectionResponse`
- 再把这个 rejection 当成本地消息分发回去

这就把“发不出去”变成了“一个可完成的失败响应”，而不是让调用方卡死。

---

## 4. `GatewayManager` 到底在管什么

关键文件：

- `src/Orleans.Core/Messaging/GatewayManager.cs`
- `src/Orleans.Core/Messaging/StaticGatewayListProvider.cs`
- `src/Orleans.Core/Messaging/IGatewayListProvider.cs`

`GatewayManager` 管的是 gateway 名单，不是连接本身。

它有三个核心状态：

- `knownGateways`
- `knownDead`
- `knownMasked`

### 4.1 gateway 名单从哪里来

两种来源：

- 配置里的静态 gateway list
- `IGatewayListProvider`

如果两者都有，provider 优先。

这点很像 Orleans 其他地方的 provider 生态：表面上是配置，实际背后都留了一个 provider 口子。

### 4.2 dead 和 masked 不是一回事

`knownDead` 表示这个 gateway 在当前时间窗口里被认为死了。

`knownMasked` 表示它暂时不该用于新发送，但还可能继续收旧响应。

这两个集合一起决定：

- 哪些 gateway 可以发新请求
- 哪些 gateway 只保留连接不再选路

这个分法挺细，也挺绕。

### 4.3 刷新列表时会顺手踢掉过期连接

`GatewayManager` 会周期性刷新 gateway 快照。

刷新后，它会把不在 live 列表里的连接关掉：

```csharp
await this.connectionManager.CloseAsync(address);
```

所以 gateway 列表、dead/masked 标记、连接池不是分开的三件事，它们是互相影响的。

### 4.4 我觉得这里不够干净的一点

`GatewayManager` 既像“名单服务”，又像“故障记忆器”，还负责一部分连接回收。

这几个职责粘在一起，能跑，但不算清爽。

如果以后自己复刻，我会把：

- gateway 发现
- gateway 健康状态
- gateway 选路策略
- gateway 连接回收

拆得更开一点。

---

## 5. `ConnectionManager` 和 `ClientOutboundConnection` 怎么配合

关键文件：

- `src/Orleans.Core/Networking/ConnectionManager.cs`
- `src/Orleans.Core/Networking/ClientOutboundConnection.cs`

`GatewayManager` 只说“哪个 gateway 可用”，真正的连接管理是 `ConnectionManager`。

### 5.1 `ConnectionManager` 负责连接对象的生命周期

它做的事包括：

- `GetConnection(endpoint)`
- 开新连接
- 复用已有连接
- 连接失败后等一段时间再试
- 关闭连接
- 记录连接终止

`GetConnection(...)` 的逻辑不是一把梭，而是：

1. 先看当前有没有可用连接
2. 没有的话再看是不是还在失败冷却期
3. 不是的话再起一个 pending connection
4. 最后等待连接建立完成

这里的重试不是无限制狂试，而是有 `ConnectionRetryDelay` 和 `OpenConnectionTimeout` 这些约束的。

### 5.2 `ClientOutboundConnection` 是真正把消息写到 socket 的那层

它继承自通用 `Connection`，但加了 client 到 gateway 这一层的逻辑。

它做的几件事很明确：

- 连接起来时先写 preamble
- 校验 cluster id
- `PrepareMessageForSend(...)` 时必要的话把 `TargetSilo` 填成当前 gateway
- 发送失败时把消息丢回 `ClientMessageCenter` 重新选路
- 如果 retry 超过上限，就把 request 变成 rejection

这也是 client 侧重试和 reject 的关键分界线：

- `ConnectionManager` 管“连接能不能建起来”
- `ClientOutboundConnection` 管“这条消息能不能从这条连接发出去”
- `ClientMessageCenter` 再决定“要不要换一条连接重新发”

### 5.3 连接断了以后，消息怎么回流

如果连接失效，`ClientOutboundConnection.OnSendMessageFailure(...)` 会把消息 `TargetSilo` 清掉，再丢回 `ClientMessageCenter.SendMessage(...)`。

这意味着：

- 失败不是直接结束
- 先让路由层再挑一次 gateway
- 真挑不到，才会落到 rejection

这条设计能提高可恢复性，但代价是链路变长，排查问题也更绕。

---

## 6. 回调链到底怎么收口

关键文件：

- `src/Orleans.Core/Runtime/CallbackData.cs`
- `src/Orleans.Core/Runtime/OutsideRuntimeClient.cs`
- `src/Orleans.Core/Networking/ClientOutboundConnection.cs`

这部分是整篇最容易被忽略，但其实最重要的地方。

### 6.1 正常响应是怎么回来的

连接收到响应以后：

1. `ClientOutboundConnection.OnReceivedMessage(...)`
2. `ClientMessageCenter.DispatchLocalMessage(...)`
3. `OutsideRuntimeClient.HandleMessage(...)`
4. `OutsideRuntimeClient.ReceiveResponse(...)`
5. 从 `callbacks` 里 `TryRemove(response.Id, out callbackData)`
6. `callbackData.DoCallback(response)`
7. `ResponseCompletionSource.Complete(...)`

整个过程里，真正收口的是 `CallbackData`。

`OutsideRuntimeClient` 只是把它找出来。

### 6.2 `CallbackData` 负责把不同失败路径统一成最终结果

`CallbackData` 不是纯数据结构，它更像一个小状态机。

它能处理四种情况：

- 正常 response
- request timeout
- cancellation
- target silo fail

这些最终都会走到：

- `context.Complete(Response.FromException(...))`
- 或者 `context.Complete(response)`

也就是说，不管是成功还是失败，最后都要回到 `ResponseCompletionSource`。

### 6.3 status update 是个旁路，不是最终完成

有一类响应是 `Message.ResponseTypes.Status`。

这不是最终返回，而是状态更新：

- callback 还留着
- 只是更新一下 `lastKnownStatus`
- 等真正的 response 或 timeout 再收口

这个设计说明 Orleans 对 status update 的定位很明确：

它是诊断和辅助信息，不是业务结果。

### 6.4 timeout、cancellation、target silo fail 的语义不一样

`CallbackData` 这三条路径虽然最后都能让 await 结束，但语义不是一回事：

- timeout：等太久了，没收到响应
- cancellation：调用方主动取消
- target silo fail：目标节点已经不可靠了

这三种错误最后各自会变成不同异常：

- `TimeoutException`
- `OperationCanceledException`
- `SiloUnavailableException`

这点挺好，至少错误类型还算分得开。

### 6.5 有个地方我觉得不够干净

callback 的生命周期是分散的：

- `OutsideRuntimeClient` 管字典
- `CallbackData` 管状态
- `ResponseCompletionSource` 管 await 完成
- `GatewayManager` / `ConnectionManager` 又会间接触发 callback 失效

它能工作，但不是一条很平的链。

如果你要自己复刻，我会考虑把 callback 生命周期再收拢一点，至少把“注册、超时、取消、断连、完成”这几类状态转移放到更统一的地方。

---

## 7. 重试、拒绝、断连这三类失败怎么分

这一段很关键，因为 Orleans client 侧看起来是“尽力送达”，但其实不同失败会落到不同出口。

### 7.1 连接还在建，但发消息失败

`ClientOutboundConnection.RetryMessage(...)` 会先看重试次数。

没超上限就重新丢回 `ClientMessageCenter.SendMessage(...)`。

超了以后就 `FailMessage(...)`，对 request 来说会变成 rejection。

### 7.2 目标 gateway 明确不可用

如果消息直接指定了 `TargetSilo`，但这条 gateway 连不上，`ConnectAsync(...)` 会直接 reject 这条消息。

这里不会偷偷帮你换目标。

### 7.3 没有可用 gateway

如果 `GatewayManager.GetLiveGateways()` 结果是空，`ClientMessageCenter` 会直接 reject request。

对 client 来说，这时就是“当前集群确实没路可走”。

### 7.4 response 回来太晚

如果 `CallbackData` 已经 timeout 并完成了 promise，后面即使 response 再回来，`callbacks.TryRemove(...)` 也可能找不到对应项。

这时只是记日志，不会再二次完成。

这也是 Orleans 这里一个比较现实的地方：

请求超时以后，系统不会再替你把那个结果硬塞回去。

---

## 8. 这条链里不够干净的地方

这块我直接说几个我认为比较明显的点。

### 8.1 路由和连接管理粘得太紧

`ClientMessageCenter` 不只是消息中心，它同时做：

- 路由选择
- bucket 管理
- 失败重试
- reject 构造
- gateway dead marking 的触发

这几个职责放在一起，短期内好用，长期看不算轻。

### 8.2 `GatewayManager` 既是名单，又是故障记忆

它不只是“有哪几个 gateway”，还要记 dead、masked、刷新周期、过期清理。

这让它很像一坨状态协调器。

### 8.3 callback 生命周期被拆得比较散

正常、超时、取消、断连、reject 都要走不同入口。

这不算 bug，但确实会让读代码的人一会儿看字典，一会儿看 `CallbackData`，一会儿看连接回调，很容易断线。

### 8.4 client 侧有不少历史包袱

像 `PeriodicTimer`、bucket routing、gateway refresh、retry filter 这些东西叠在一起后，链路已经不是“发消息”这么简单了。

如果目标是重写一个更干净的版本，我会倾向把：

- gateway discovery
- gateway selection
- connection lifecycle
- request callback lifecycle

四层拆得更开一点。

---

## 9. 如果你要自己复刻，我会怎么拆

如果目标是完整复刻，但比 Orleans 更干净，我会把这一块拆成四层。

### 9.1 Gateway Discovery

只负责：

- 从 provider 拿 gateway 列表
- 做 refresh
- 维护 live / dead / masked 状态

### 9.2 Connection Pool

只负责：

- 建连
- 复用
- 失败重试
- 关闭

不要顺手掺路由逻辑。

### 9.3 Request Router

只负责：

- 选哪条 connection
- 普通请求 bucket 化
- unordered / system target 的特殊路由

### 9.4 Callback Registry

只负责：

- 注册 callback
- 完成 callback
- 超时处理
- cancellation
- target fail

这样做的好处是，出问题时你能很快知道是哪一层坏了。

---

## 10. 这一篇最该反复看的源码文件

建议按这个顺序读：

1. `src/Orleans.Core/Runtime/OutsideRuntimeClient.cs`
2. `src/Orleans.Core/Messaging/ClientMessageCenter.cs`
3. `src/Orleans.Core/Messaging/GatewayManager.cs`
4. `src/Orleans.Core/Networking/ConnectionManager.cs`
5. `src/Orleans.Core/Networking/ClientOutboundConnection.cs`
6. `src/Orleans.Core/Runtime/CallbackData.cs`
7. `src/Orleans.Serialization/Invocation/ResponseCompletionSource.cs`
8. `src/Orleans.Core/Runtime/GrainReferenceRuntime.cs`
9. `src/Orleans.Core/Messaging/MessageFactory.cs`

如果把这 9 个文件按链路顺着看完，client 侧发送、收响应、断连重试、超时收口这几件事就基本能看透了。

---

## 11. 这一篇的落点

把这一篇压成一句话，就是：

Orleans client 侧不是“发一条消息然后等结果”，而是一套完整的消息路由和回调管理系统。

`OutsideRuntimeClient` 负责把请求和 callback 绑起来，`ClientMessageCenter` 负责把消息送到合适的 gateway，`GatewayManager` 和 `ConnectionManager` 负责维护可用连接，`CallbackData` 负责在响应、超时、取消、断连和拒绝之间收口。

这条链能跑得很稳，但代价也很明显：

- 状态很多
- 入口很多
- 失败路径很多
- 边界不算特别干净

如果你想复刻一版，我会建议把这块做得比 Orleans 更直一点。至少别把“路由、连接、回调、失败处理”四件事全揉成一个大网。

