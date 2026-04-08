# 如果不是复刻 Orleans，而是重建一版，应该继承什么、重写什么、延后什么（第三十三篇）

这一篇不再问“最小子集怎么裁”。

我想直接换一个更适合你现在目标的问题：

> 如果目标不是做一个“尽量像 Orleans 的复制品”，而是做一个吸收了 Orleans 经验、主动绕开历史包袱的新运行时，到底该怎么取舍。

先把结论说死：

- 不要“复刻 Orleans”，要“提炼 Orleans 内核”。
- 只继承那些已经被 Orleans 多轮迭代证明过、今天仍然站得住的东西。
- 主动放弃历史兼容、总装配式注册、运行时扫描发现、过厚的特例层。
- 先把运行时内核做成几块责任清楚的抽象，再往上挂 capability module。
- 第一版目标不是功能全，而是内核干净、边界稳、后面能长。

如果压成一句话，就是：

> 你应该复制 Orleans 的“成熟语义”，而不是复制 Orleans 的“现有形状”。

这两件事差别非常大。

---

## 1. 先讲我对“该继承什么”的判断

从前面 1 到 32 篇一路拆下来，我觉得 Orleans 真正成熟、值得继承的，不是某几个类，而是下面这 6 个核心判断。

### 1.1 强类型引用，而不是对象直调

这一点必须继承。

也就是：

- `GrainId`
- `GrainType`
- `GrainInterfaceType`
- `GrainReference`
- 强类型代理

这是 Orleans 最根上的设计正确性来源。

它让系统天然有：

- 远程调用语义
- 可路由性
- 可缓存性
- 可版本化
- 可诊断性

如果把这层拿掉，你做出来的就不是 Orleans 路线，而更像一个“对象版 RPC 框架”。

### 1.2 `Proxy -> IInvokable -> Message` 这条调用降级链

这一点也值得继承。

用户写的是：

```csharp
await grain.Foo(x, y)
```

运行时看到的是：

```text
强类型代理
  -> IInvokable
  -> Request / Response
  -> Message
```

这条链很值钱，因为它把：

- 调用捕获
- 参数序列化
- 路由
- 重试/拒绝
- 返回值完成

放进了同一个协议面。

这是一条好抽象，不该丢。

### 1.3 turn-based 调度模型

也就是 `WorkItemGroup` 背后的那套思想。

我建议你保留这个语义，但不要照抄 Orleans 现在那一大坨实现。

该保留的是：

- activation 是单线程执行单元
- 默认串行
- 允许受控 interleaving
- 调度语义是运行时协议的一部分

不该直接继承的是：

- 太多层的 reentrancy 特判
- 过多上下文标记
- 调度规则散在多个对象里

换句话说，保留“actor-ish turn model”，重写“现在这套具体调度拼法”。

### 1.4 编译期代码生成 + manifest 驱动运行时

这一点我认为是 Orleans 近几年最成熟的一条主线。

真正值得继承的是这套闭环：

```text
编译期
  -> 生成 proxy / invokable / serializer / copier / activator / metadata

运行时
  -> 读取 manifest
  -> 按 manifest 找实现
  -> 少做或不做运行时猜测
```

这条路天然比“运行时多反射、多扫描、多猜测”更适合：

- 性能
- 裁剪
- AOT
- 可验证性
- 兼容性治理

如果你要重建，我会把这条路走得比 Orleans 更激进。

### 1.5 激活模型，而不是“单例对象池”

Orleans 的 `ActivationData` 很重，但 activation 这个概念本身是对的。

你应该保留：

- activation 是执行边界
- activation 有自己的生命周期
- activation 有自己的调度队列
- activation 能接消息、退场、迁移、收回

但你不应该继承：

- 一个 `ActivationData` 扛几乎所有职责

也就是：

保留 activation，拆掉 `ActivationData` 的肥度。

### 1.6 host/lifecycle 是显式协议

Orleans 这点虽然现在实现不够漂亮，但方向是对的。

运行时不是“new 一堆对象然后随便跑起来”，而是：

- 宿主层负责组合
- lifecycle 负责顺序
- capability module 负责按阶段挂载

这个原则一定要保留。

但具体实现上，我建议你不要抄 `DefaultSiloServices` / `DefaultClientServices` 那种总装配表。

---

## 2. 再讲“什么一定不要继承”

这部分其实比前一部分更重要。

因为如果你真的打算重建一版，最大的收益不在“选对要学的地方”，而在“明确不复制哪些遗产”。

### 2.1 不要继承历史兼容包袱

这一类我建议直接砍掉：

- `GenerateCompatibilityInvokers`
- 老 serializer 兼容链
- 各种 legacy grain id / 旧类型名兼容
- 为旧行为保留的属性语义分叉

这些东西之所以存在，是因为 Orleans 要对现实世界的老用户负责。

你现在不是在维护 Orleans 主线，而是在新建一版。

那就不要把“对老版本负责”变成你的第一天债务。

### 2.2 不要继承总装配式注册

也就是这类东西：

- `DefaultSiloServices`
- `DefaultClientServices`

它们在 Orleans 里是必要现实，但不应该成为新系统的起点。

如果你的新系统一开始就有一个几百行的“全部默认注册总表”，那基本等于把 Orleans 最不干净的部分直接抄过来了。

新系统更应该做的是：

- 内核模块分层
- 模块显式依赖
- 启动图可见
- 可选模块单独挂载

### 2.3 不要继承运行时扫描发现

这类能力都应该尽量后移到编译期或显式配置：

- 扫程序集
- 读 attribute 再猜模块
- `Assembly.Load`
- 按类型名运行时发现 provider / manifest / 扩展

你已经看过 Orleans 这条链了，知道它最后为什么会变复杂。

所以新系统应该从一开始就坚持：

> 运行时只消费显式登记过的 manifest，不做太多“猜”。

### 2.4 不要继承“消息壳一套、对象序列化一套、中间再夹很多手写特例”

Orleans 现在这里不是不能用，但不够整齐。

新系统我会建议这样做：

- 消息 envelope 单独一套协议
- header 有明确 schema
- body 严格走 codec
- 不要再夹像 `RequestContext` 这种半手写半通用的灰区

这会让协议层和对象层的边界清很多。

### 2.5 不要继承一上来就分布式全开

第一版直接做这些，风险太大：

- membership
- 分布式 grain directory
- placement 策略族
- gateway
- rolling upgrade
- multi-cluster
- rebalancer

这些当然最终都要有，但它们不该定义你的第一阶段架构。

第一阶段架构应该由“单机内核”定义，第二阶段才是“集群能力”。

---

## 3. 如果让我重新设计，我会怎么切抽象

这一段是关键。

如果要最大程度做好抽象，我会把系统切成 8 层，而且每一层只回答一个问题。

## 3.1 Identity Layer

只回答：

> 这个可调用对象是谁？

只放这些概念：

- `GrainId`
- `GrainType`
- `InterfaceType`
- `ReferenceId`

不要把目录、放置、版本、集群成员关系掺进来。

## 3.2 Invocation Layer

只回答：

> 一次调用被怎样捕获、表示、完成？

只放：

- proxy
- `IInvokable`
- `Request`
- `Response`
- completion source

不要在这一层掺 transport、membership、directory。

## 3.3 Serialization Layer

只回答：

> 对象怎样变成稳定的 wire form？

只放：

- manifest
- field id
- alias
- codec
- copier
- activator
- type name system

不要再把 provider 发现、运行时扫描、兼容层堆进去。

## 3.4 Activation Layer

只回答：

> 一个 activation 怎样接消息、执行、退场？

只放：

- activation host
- mailbox
- scheduler
- lifecycle
- local instance binding

这层就是新系统的心脏。

## 3.5 Routing Layer

只回答：

> 一条消息怎样找到目标 activation？

只放：

- directory
- placement
- routing cache
- local vs remote addressing

不要把 membership 和负载治理混在这里。

## 3.6 Cluster Layer

只回答：

> 集群视图怎样维护？

只放：

- membership
- failure detector
- cluster view
- liveness

## 3.7 Capability Layer

只回答：

> 哪些是内核之外的能力模块？

放：

- storage
- reminders
- streaming
- transactions
- diagnostics
- security

这些都应该是挂件，不该反过来定义内核。

## 3.8 Host Composition Layer

只回答：

> 系统怎么被组装并启动？

只放：

- builder
- module registration
- lifecycle graph
- config binding

不要把运行时逻辑直接写进这一层。

---

## 4. 第一版应该长什么样

如果是我自己重建，我会把第一版目标压得非常狠。

不是“尽量多做”，而是“只做一个干净的内核”。

### 第一版必须有

- 单集群
- 单二进制协议
- 强类型 grain reference
- 编译期 proxy / invokable / serializer 生成
- manifest 驱动的 runtime
- 本地 activation + mailbox + turn scheduler
- 最薄的 catalog
- 最薄的 directory/placement
- host/lifecycle
- in-memory transport 或 loopback transport

### 第一版不要有

- gateway
- client/silo 分离部署
- 多版本兼容
- streaming
- reminders
- transactions
- event sourcing
- resource rebalancer
- provider 生态
- observer / extension 全家桶

这些都可以第二阶段再补。

第一版唯一目标是：

```text
GetGrain
  -> proxy
  -> IInvokable
  -> message
  -> local routing
  -> activation execute
  -> response
```

先把这条链做成一个你自己都愿意继续往上加东西的内核。

---

## 5. 第二版和第三版再加什么

### 第二版：从单机内核走向单集群运行时

这一版再加：

- membership
- remote transport
- distributed directory
- placement strategy
- client/gateway
- deactivation / collection
- storage

也就是说，第二版才开始真正像 Orleans。

### 第三版：能力模块和运维能力

这一版再加：

- reminders
- streaming
- diagnostics
- security
- rolling upgrade
- compatibility policy

### 第四版：重特性

最后再看：

- transactions
- multi-cluster
- event sourcing
- resource rebalance
- 更完整的 provider 生态

这个顺序的核心思想是：

> 先做语义内核，再做分布式，再做生态，再做高级特性。

不要一开始就站在 Orleans 现在的位置往回推，那样很容易直接把历史厚度全继承过来。

---

## 6. 我会明确保留的“成熟实现思路”

如果你让我点名，我会保留下面这些 Orleans 思路。

### 6.1 保留 grain reference 语义

不是对象代理本身，而是：

- 身份驱动
- 强类型接口
- 引用优先于实例

### 6.2 保留 invokable 模型

这比“方法名 + object[] 参数”强太多了。

### 6.3 保留 field id + alias + manifest 这套序列化思路

这是 Orleans 在长期演进里很值钱的一部分。

### 6.4 保留 turn scheduler

但重写实现，让规则更少、更显式。

### 6.5 保留 lifecycle stage

但把 stage 图做成一等公民，不要藏在很多层服务注册里。

### 6.6 保留 host integration

但把 builder/module graph 做得比 Orleans 清楚。

---

## 7. 我会明确重写的地方

### 7.1 重写装配层

不要 `DefaultSiloServices` 风格的大总表。

### 7.2 重写 activation 宿主

把 `ActivationData` 拆成：

- activation state
- mailbox
- scheduler host
- lifecycle host
- local binding

别让一个类扛一切。

### 7.3 重写 routing 边界

把：

- directory
- placement
- cache
- activation lookup

边界切清楚，不要像 Orleans 一样层层互相渗。

### 7.4 重写协议边界

消息 envelope 和 body codec 分开，别再走半手写半通用那种灰区。

### 7.5 重写扩展机制

我会把 extension 和 observer 做成明确分层，不让“引用”语义再混成三套半像不一样的东西。

---

## 8. 我会明确延后的地方

这部分你最好真的忍住。

先延后：

- 多集群
- 事务
- 复杂兼容
- rolling upgrade
- rebalancer
- provider 生态
- benchmark 体系
- fault injection 平台

不是因为它们不重要，而是因为它们太容易把早期设计拖歪。

如果你第一版架构还没稳定，就上这些，最后很可能只是“做出一个更小的 Orleans”，而不是“做出一个更干净的新系统”。

---

## 9. 给你的最终建议

如果目标真的是“重建而不是复刻”，那我建议你内部直接换一个口径：

不要说“我要重写 Orleans”。

要说：

> 我要做一个以 Orleans 为老师、但不继承其历史债务的 actor/runtime kernel。

这样你做设计决策时会更自由。

你就不会总想着：

- 这个类 Orleans 里有，我是不是也要有
- 这个 feature Orleans 支持，我是不是也得现在就支持
- 这个兼容层 Orleans 留着，我是不是也先带上

真正该问的是：

- 这个语义是不是成熟的
- 这个抽象是不是值得保留
- 这个复杂度是问题本身带来的，还是 Orleans 历史演进带来的

前者该学。

后者该绕开。

---

## 10. 这一篇的落点

这一篇最后想说的其实只有一句：

Orleans 最值得你继承的，不是它现在这棵长成什么样的树，而是它这么多年踩坑之后，哪些树干已经被证明是对的。

我自己的答案是：

- 继承语义内核
- 重写实现骨架
- 删除历史兼容
- 推迟外围生态
- 用更显式的模块边界重新组织系统

如果你按这个思路走，最后做出来的东西就会是：

- 像 Orleans
- 但不会被 Orleans 的遗产形状锁死

这才是“重建一版”真正该追求的目标。

