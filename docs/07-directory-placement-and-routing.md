# 目录、放置与寻址：GrainLocator、GrainDirectory、PlacementService 是怎么串起来的（第七篇）

这一篇专门讲一条最容易被看乱的链：

第一次调用一个 Grain 的时候，Orleans 到底怎么知道该发到哪里。

先把结论说清楚：

- `PlacementService` 不是目录，它负责的是“这条消息先送到哪个 Silo”。
- `GrainDirectory` 不是放置策略，它负责的是“这个 Grain 的 activation 现在登记在哪个 Silo”。
- `GrainLocator` 是两者之间的统一门面，客户端、运行时、目录缓存更新都从它这里走。
- 第一次调用时，通常先查本地目录缓存；缓存没有命中，再查目录；目录没有命中，再走放置，最后由目标 Silo 上的 `Catalog` 创建 activation。
- 目录缓存不是装饰品，它直接决定大多数请求能不能少跑一轮全局查找。
- 缓存失效也不是事后补丁，它是消息处理链的一部分，响应、转发、拒绝都会带着 cache invalidation 信息往回传。

如果你只记一句话，就是这个：

> Placement 负责选路，Directory 负责登记，Cache 负责提速，Catalog 负责创建。

这四个东西分工不清，Orleans 就看不顺。

---

## 1. 先把整条主链压成一张图

```text
用户调用 grain 方法
  -> 代理对象把调用变成 Message
  -> RuntimeClient.SendRequest
  -> MessageCenter.AddressAndSendMessage
  -> PlacementService.AddressMessage
       -> 先查 GrainLocator 本地缓存
       -> 命中则直接写 TargetSilo
       -> 没命中则进入 PlacementWorker
            -> 先查 GrainLocator.Lookup
            -> 再查 PlacementStrategy / PlacementDirector
            -> 选出目标 Silo
            -> 反向再查一次缓存
            -> 仍然没有更好的地址，就更新本地缓存
  -> MessageCenter.SendMessage
  -> 网络 or 本地回环
  -> 目标 Silo MessageCenter.ReceiveMessage
  -> Catalog.GetOrCreateActivation
       -> 先查 ActivationDirectory
       -> 没有就创建 activation
       -> activation 进入运行态
  -> ActivationData.ReceiveRequest
  -> 真正执行 grain 方法
```

这条链里有一个很容易混淆的点。

`PlacementService` 和 `GrainDirectory` 都会碰到“找地址”这件事，但它们不是同一个层次。

- `PlacementService` 解决的是请求现在该被送到哪台机器。
- `GrainDirectory` 解决的是某个 grain 的 activation 已经登记到哪台机器。

前者是路由，后者是登记。

---

## 2. 目录和放置到底怎么分工

### 2.1 `PlacementService` 做什么

关键文件：

- `src/Orleans.Runtime/Placement/PlacementService.cs`

它对外的主入口是：

- `AddressMessage(Message message)`
- `PlaceGrainAsync(...)`

这两个方法看起来都在“找地址”，但语义不同。

`AddressMessage(...)` 面对的是一条已经准备发送的消息。它先看 `TargetGrain` 能不能直接从 `GrainLocator` 的缓存里拿到地址，能拿到就直接把 `TargetSilo` 填好。

拿不到的话，才把消息交给 `PlacementWorker` 做更完整的处理。

`PlaceGrainAsync(...)` 则是纯放置逻辑。它不看已有 activation 的位置，而是直接问 placement director：如果现在要新建一个 activation，该放到哪台 Silo。

### 2.2 `GrainDirectory` 做什么

关键文件：

- `src/Orleans.Runtime/GrainDirectory/GrainLocator.cs`
- `src/Orleans.Runtime/GrainDirectory/GrainDirectoryResolver.cs`
- `src/Orleans.Runtime/GrainDirectory/LocalGrainDirectory.cs`
- `src/Orleans.Runtime/GrainDirectory/DistributedGrainDirectory.cs`

`GrainDirectory` 这条线负责的是注册和查询。

它不决定“这个 grain 应该放哪儿”，它只记录“已经放哪儿了”。

也就是说，目录的核心动作只有三个：

- `Lookup`
- `Register`
- `Unregister`

其它东西，比如 ring partition、缓存更新、成员变更、快照迁移，都是围绕这三个动作展开的。

### 2.3 `GrainLocator` 为什么要单独存在

`GrainLocator` 其实是一个门面。

它把不同 grain type 的定位逻辑统一起来：

- client grain 走 `ClientGrainLocator`
- 默认目录 grain 走 `DhtGrainLocator`
- 其它 grain type 走 `CachedGrainLocator`

所以它不是单一目录实现，而是“按 grain type 分流”的入口。

这也是 Orleans 这块比较绕的原因之一：

你看到的是一个 `Lookup(...)`，背后其实先经过了一次 locator 选择。

---

## 3. 第一次调用是怎么找到 activation 的

这部分是整篇最重要的。

### 3.1 第一步：消息先进入 `MessageCenter`

关键文件：

- `src/Orleans.Runtime/Messaging/MessageCenter.cs`

对外发消息时，`MessageCenter.AddressAndSendMessage(...)` 会先调用 `PlacementService.AddressMessage(message)`。

如果消息已经有 `TargetSilo`，那就不需要放置。

如果没有，`PlacementService` 先尝试用 `GrainLocator.TryLookupInCache(...)` 命中本地缓存。

命中就结束了。

这就是第一次调用里最便宜的一条路。

### 3.2 第二步：缓存没命中，进入 placement worker

`PlacementService` 里有一组 `PlacementWorker`，按 grain hash 分桶。

这个 worker 做的事情不是直接“分配新激活”，而是先把同一个 grain 的多条消息合并起来，避免大家都去争同一件事。

worker 的流程大概是：

1. 收集同一个 `GrainId` 的消息
2. 对第一条消息做真正的寻址
3. 后续消息复用同一个结果
4. 一旦结果出来，把 `TargetSilo` 写回所有待处理消息

这一步的核心是减少重复工作。

### 3.3 第三步：先查目录，再决定要不要新建

`PlacementWorker.GetOrPlaceActivationAsync(...)` 里先做的是：

```csharp
var result = await _placementService._grainLocator.Lookup(targetGrain);
```

如果目录里已经有地址，就直接返回。

如果没有，就进入 placement director：

```csharp
var strategy = _placementService._strategyResolver.GetPlacementStrategy(...)
var director = _placementService._directorResolver.GetPlacementDirector(strategy)
var siloAddress = await director.OnAddActivation(...)
```

这一步很关键。

它说明 Orleans 的“目录查找”和“放置决策”是连着走的：

- 有现成 activation，就按目录走
- 没有现成 activation，就按策略选一个 Silo 去创建新的 activation

### 3.4 第四步：放置完以后，再给缓存一个机会

这段代码很有 Orleans 味道。

新 Silo 选出来以后，它不会立刻就当成最终答案，而是再查一次：

```csharp
if (_placementService._grainLocator.TryLookupInCache(targetGrain, out result) && _placementService.CachedAddressIsValid(firstMessage, result))
{
    return result.SiloAddress;
}

_placementService._grainLocator.InvalidateCache(targetGrain);
_placementService._grainLocator.UpdateCache(targetGrain, siloAddress);
return siloAddress;
```

这段逻辑的意思是：

- 如果别的路径已经更快地把这个 grain 放好了，就直接用最新地址
- 如果还没有，就把自己刚选出的 Silo 写进缓存

这就是所谓“第一次调用”的完整路径。

不是单纯“没找到就创建”，而是“先找、再放、再复查、最后写缓存”。

---

## 4. 目录缓存到底在干什么

### 4.1 缓存不是一个点，是一层分布式本地加速

关键文件：

- `src/Orleans.Runtime/GrainDirectory/IGrainDirectoryCache.cs`
- `src/Orleans.Runtime/GrainDirectory/LruGrainDirectoryCache.cs`
- `src/Orleans.Runtime/GrainDirectory/GrainDirectoryCacheFactory.cs`
- `src/Orleans.Runtime/GrainDirectory/CachedGrainLocator.cs`

`IGrainDirectoryCache` 只负责四件事：

- `AddOrUpdate`
- `Remove`
- `Clear`
- `LookUp`

默认实现是 LRU，也可以配成空缓存或者自定义缓存。

这说明目录缓存不是强约束结构，只是性能层。

### 4.2 缓存命中的地方很多

`CachedGrainLocator.Lookup(...)` 的第一步就是查本地 cache。

如果命中，并且这个地址对应的 silo 还活着，就直接返回。

如果没命中，才去目录里做真正的 lookup。

所以目录缓存不是“可有可无的优化”，而是这里真正决定吞吐的第一道门。

### 4.3 缓存会主动跟着 membership 变化清理

`CachedGrainLocator` 会订阅 cluster membership 变化。

当它发现有 silo 进入 terminating 状态时，会把相关目录实例上的旧地址批量清掉。

这就是为什么 Orleans 的目录缓存不能只看成简单字典：

它是和 membership 联动的。

如果 membership 不变，缓存很像普通 LRU。

一旦 membership 在动，它就变成了一层需要被主动校正的路由状态。

### 4.4 DHT 目录里的 cache 又是另一层

对于默认目录，`DhtGrainLocator` 实际上把 cache 放在 `LocalGrainDirectory` 里。

这意味着：

- client 侧有 client locator
- runtime 侧有 locator cache
- 默认 grain directory 的本地实现里又有一层 local cache

这几层缓存名字很像，职责却不完全一样。

这也是 Orleans 这条线最容易绕的地方之一。

---

## 5. 地址缓存失效是怎么传播的

### 5.1 失效消息不是附加信息，它是协议的一部分

关键文件：

- `src/Orleans.Core/Messaging/Message.cs`
- `src/Orleans.Runtime/Messaging/MessageCenter.cs`
- `src/Orleans.Runtime/Core/InsideRuntimeClient.cs`

`Message` 里有一个单独的：

- `CacheInvalidationHeader`

这不是单纯的日志字段，而是消息协议的一部分。

它会在几类情况下出现：

- activation 转发到新地址
- activation 被拒绝
- activation 已失效
- one-way 消息需要顺手把缓存同步回去

### 5.2 转发时会带上旧地址和新地址

`MessageCenter.TryForwardRequest(...)` 会在转发前写入：

- invalid address
- valid address

也就是说，收到这条消息的一端不只是知道“这个地址失效了”，还知道“它应该更新成什么地址”。

### 5.3 收到响应后，客户端和运行时都会更新本地缓存

`InsideRuntimeClient.ReceiveResponse(...)` 里对 rejection 的处理会看：

- 如果带了 `CacheInvalidationHeader`，直接按 header 更新缓存
- 如果没带，就退回旧逻辑，把发送方地址从本地 cache 里清掉

这说明 cache invalidation 不是只发生在目录层，而是消息层、运行时层都在配合。

### 5.4 activation 自己也会触发失效

`ActivationData` 在退化、迁移、重定向时也会显式调用：

- `GrainLocator.InvalidateCache(Address)`
- `GrainLocator.UpdateCache(...)`

也就是说，缓存失效不只是“别人告诉我错了”，activation 自己在生命周期变化时也会主动推。

这就形成了一个闭环：

1. 目录给出地址
2. 消息发送时把地址写进消息
3. activation 迁移或失效时把新旧地址写回消息
4. 接收端再更新本地缓存

---

## 6. 目录本体：`LocalGrainDirectory` 和 `DistributedGrainDirectory`

### 6.1 `LocalGrainDirectory` 是运行时的本地目录实现

关键文件：

- `src/Orleans.Runtime/GrainDirectory/LocalGrainDirectory.cs`

它干的是三件事：

- 维护本地 ring membership
- 维护本地 activation 目录
- 负责 lookup/register/unregister 的本地和远程转发

它有一个很明显的两阶段模型：

- 正常阶段直接处理
- membership 变化阶段做迁移和清理

这里的代码能看出来，目录不是一个冷冰冰的 key-value store，它是跟集群成员变化绑在一起的状态机。

### 6.2 `DistributedGrainDirectory` 才是更像“目录服务本体”的东西

关键文件：

- `src/Orleans.Runtime/GrainDirectory/DistributedGrainDirectory.cs`
- `src/Orleans.Runtime/GrainDirectory/GrainDirectoryPartition.cs`

这块代码里最重要的信息就是：目录是按 consistent hash ring 分片的。

一个 grain 的 directory owner 由它的 hash 和当前 membership view 决定。

如果 owner 不是本机，就把请求转发过去。

如果 owner 就是本机，就在本地 partition 里完成 register / lookup / unregister。

### 6.3 `GrainDirectoryPartition` 才是那层真正的“分片”

`GrainDirectoryPartition` 里有几个很关键的概念：

- range
- view / membership version
- snapshot transfer
- range lock

这说明 directory 不是单纯的一张表，而是一个带 view change 的分布式分片结构。

目录的职责不是只记地址，它还要处理：

- 分片 owner 切换
- 旧 owner 到新 owner 的快照迁移
- 恢复注册
- 视图版本不一致时的重试

这比一般人理解的“directory”重得多。

---

## 7. 目录与放置的边界，到底在哪里

这一节是这篇最想说明白的部分。

### 7.1 放置是预测，目录是事实

`PlacementService` 做的是预测：

> 如果现在要放一个新的 activation，应该放到哪台 Silo？

`GrainDirectory` 做的是事实登记：

> 这个 grain 的 activation 现在已经登记在哪台 Silo？

所以：

- 放置可能先于目录
- 目录可能滞后于放置
- 缓存可能短时间领先或者落后于目录

Orleans 这套代码并不假装它们永远一致，它只是尽量把这些不一致收敛掉。

### 7.2 `PlacementService` 也会碰目录，但它不是目录

`PlacementService.AddressMessage(...)` 会先查 cache，再查目录，再放置。

这看起来像“它也在做目录的活”，但本质不是。

它之所以要碰目录，是因为它必须在把消息送出去之前，先知道目标 Silo。

它不是目录 owner，也不负责注册数据的持久一致性。

### 7.3 `GrainLocator` 把这条边界模糊掉了

`GrainLocator` 把 lookup、register、invalidate、update cache 都收进一个入口。

这让上层调用很省事，但也让职责边界看起来没那么清楚。

在源码里你会感觉：

- `PlacementService` 在查缓存
- `Catalog` 在 invalidate cache
- `ActivationData` 在 update cache
- `MessageCenter` 在做 cache invalidation header

这些都从同一个 `GrainLocator` 入口出去。

这就是 Orleans 的一个典型风格：

对调用方很方便，对读源码的人就不太友好。

---

## 8. 这条链里我觉得不够干净的地方

### 8.1 `PlacementService` 管得太多

它不只是放置策略入口，还背了：

- cache 命中
- 目录 lookup
- worker 合并
- invalidation 校验
- 消息目标写回

这会让“放置”这个概念被拉得很宽。

如果以后自己复刻，我会把“查目录”和“选放置目标”拆得更清楚一点。

### 8.2 缓存层有点太多了

你会同时看到：

- `GrainLocator` cache
- `IGrainDirectoryCache`
- `LocalGrainDirectory.DirectoryCache`
- `ClientGrainLocator` 的本地目录

这些缓存各自都有合理性，但叠起来以后，语义就不够直。

尤其是出问题时，你很难一眼判断到底是哪一层缓存错了。

### 8.3 目录和消息协议互相渗透

按理说，目录应该只是“记录位置”。

但 Orleans 这里，消息转发、拒绝、缓存失效、回包更新，都会反向影响目录状态。

这没错，只是说明目录不是一个纯粹的数据结构，而是和消息系统绑死的运行时子系统。

### 8.4 默认目录实现和抽象目录边界有点不一致

`LocalGrainDirectory`、`DistributedGrainDirectory`、`GrainDirectoryPartition` 三层之间有不少耦合。

尤其是 view change 和 recovery 的代码，已经远超过一个简单目录服务该承载的复杂度。

这也是 Orleans 架构里最“历史包袱感”很强的一块。

---

## 9. 如果要复刻，我会怎么拆

如果目标是复刻一版更干净的实现，我建议这块至少拆成四层：

### 9.1 Routing Layer

只做：

- 消息目标 Silo 选择
- 本地缓存查询
- 发送前目标校验

### 9.2 Registry Layer

只做：

- lookup
- register
- unregister
- cache invalidation 数据维护

### 9.3 Placement Layer

只做：

- placement strategy
- director
- candidate selection

### 9.4 Membership / Partition Layer

只做：

- owner 计算
- view change
- snapshot transfer
- recovery

这样拆完以后，路由、目录、放置、成员变化会清楚很多。

Orleans 现在的问题不是功能不够，而是这些职责互相穿插得太厉害。

---

## 10. 这一篇最该反复看的源码文件

建议按这个顺序读：

1. `src/Orleans.Runtime/Placement/PlacementService.cs`
2. `src/Orleans.Runtime/Messaging/MessageCenter.cs`
3. `src/Orleans.Runtime/GrainDirectory/GrainLocator.cs`
4. `src/Orleans.Runtime/GrainDirectory/GrainLocatorResolver.cs`
5. `src/Orleans.Runtime/GrainDirectory/CachedGrainLocator.cs`
6. `src/Orleans.Runtime/GrainDirectory/DhtGrainLocator.cs`
7. `src/Orleans.Runtime/GrainDirectory/LocalGrainDirectory.cs`
8. `src/Orleans.Runtime/GrainDirectory/DistributedGrainDirectory.cs`
9. `src/Orleans.Runtime/GrainDirectory/GrainDirectoryPartition.cs`
10. `src/Orleans.Runtime/GrainDirectory/IGrainDirectoryCache.cs`
11. `src/Orleans.Runtime/Catalog/Catalog.cs`
12. `src/Orleans.Runtime/Catalog/ActivationData.cs`
13. `src/Orleans.Core/Messaging/Message.cs`
14. `src/Orleans.Runtime/Core/InsideRuntimeClient.cs`

如果你把这 14 个文件顺着过一遍，再回头看第二篇“从 `GetGrain` 到方法返回”的调用链，你就会发现一个很重要的事实：

Orleans 不是先找到了 activation 再去发消息。

它是先把消息路由出去，再在路由过程中不断把“应该去哪儿”和“已经在哪儿”这两件事修正到接近一致。

---

## 11. 这一篇的落点

这条链最值得记住的不是某个类名，而是三层关系：

- `PlacementService` 决定下一跳
- `GrainDirectory` 记录事实地址
- `GrainLocator` 把两者和缓存连起来

第一次调用时，系统会先试图用缓存和目录把事情解决掉；只有这两层都没给出答案，才会真正走放置和激活创建。

而一旦 activation 真的起来了，后面的消息、转发、拒绝、迁移、失效，又会把最新地址一点点写回缓存和目录。

这就是 Orleans 这条链的真实样子。

它能跑得快，是因为缓存和路由做了很多事。

它看起来不够干净，也是因为这些事被揉在了一起。
