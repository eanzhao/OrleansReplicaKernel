# 序列化兼容性与版本演进：field id、alias、容错反序列化是怎么撑住升级的（第十八篇）

这一篇专门讲 Orleans 为什么在“兼容性”这件事上写得这么重。

先把结论说直白一点：

- Orleans 的兼容性不是一把总开关，而是几层东西叠出来的。
- `field id` 负责字段重排、增删、搬家时还能读老数据。
- `alias` 负责类型名、方法名、invokable 名字改了以后，wire 还能认得出来。
- `TypeConverter` / `TypeCodec` 负责把 CLR 类型名稳定地写进字节流，再稳定地读回来。
- `SerializerConfigurationAnalyzer`、`IdClashAttributeAnalyzer`、`AliasClashAttributeAnalyzer` 这类检查器，负责在编译或启动时尽量把坑提前拦住。
- 节点级版本兼容不是靠序列化硬撑，而是靠接口版本、版本选择器、兼容 director、placement 和 activation 退场这几层一起兜底。

如果只记一句话，就是这个：

> Orleans 不是在“让旧数据还能读”这件事上做了点补丁，而是把字段、类型、方法、接口、节点版本，全都按兼容演进来设计了。

---

## 1. 先把整条链压成一张图

```text
编译期
  -> [GenerateSerializer]
  -> [Id] / [Alias] / [CompoundTypeAlias]
  -> GenerateFieldIds / GenerateCompatibilityInvokers
  -> FieldIdAssignmentHelper
  -> SerializerGenerator / InvokableGenerator / MetadataGenerator
  -> analyzers: GenerateSerializationAttributesAnalyzer / IdClashAttributeAnalyzer / AliasClashAttributeAnalyzer

运行时写入
  -> Writer.WriteFieldHeader(...)
  -> SchemaType.Expected / WellKnown / Referenced / Encoded
  -> TypeCodec.WriteEncodedType(...)
  -> TypeConverter.Format(...)
  -> generated codec / surrogate codec / TypeSerializerCodec

运行时读取
  -> FieldHeaderCodec.ReadFieldHeader(...)
  -> TypeCodec.TryRead(...)
  -> TypeConverter.TryParse(...)
  -> generated codec / surrogate codec / TypeSerializerCodec
  -> ReferenceCodec.ReadReference(...)

节点互通
  -> GrainVersionManifest
  -> VersionSelectorManager
  -> CompatibilityDirectorManager
  -> CachedVersionSelectorManager
  -> PlacementService
  -> ActivationData 版本检查
  -> 不兼容则 deactivate + cache invalidation + 重新路由
```

这张图里最容易混的地方有两个。

第一，字段兼容和节点兼容不是一个东西。字段兼容是在“对象怎么写进字节流”这一层，节点兼容是在“这个请求该发给哪个版本的 activation”这一层。

第二，`alias` 不是装饰。它是 Orleans 兼容性里很实用的一层稳定名字机制，少了它，类型挪命名空间、接口改名字、方法重组都会更脆。

---

## 2. 为什么这块会写得这么重

关键文件：

- `src/Orleans.Serialization.Abstractions/Annotations.cs`
- `src/Orleans.CodeGenerator/CodeGenerator.cs`
- `src/Orleans.Serialization/TypeSystem/TypeConverter.cs`
- `src/Orleans.Runtime/Versions/Compatibility/CompatibilityDirectorManager.cs`

Orleans 的目标不是“只跑当前版本”。

它要面对的是这些情况同时存在：

- 旧 silo 还在跑
- 新 silo 已经滚起来了
- 集群里有不同版本的 grain 实现
- 有些类型名被重命名了
- 有些字段被拆了、挪了、补了
- 有些接口方法搬到基类或者派生接口里了

这就逼着它把兼容性拆成很多层。

如果只靠一套 serializer，根本兜不住。

---

## 3. `field id` 才是序列化兼容的地基

关键文件：

- `src/Orleans.Serialization.Abstractions/Annotations.cs`
- `src/Orleans.CodeGenerator/FieldIdAssignmentHelper.cs`
- `src/Orleans.Analyzers/GenerateSerializationAttributesAnalyzer.cs`
- `src/Orleans.Analyzers/IdClashAttributeAnalyzer.cs`
- `src/Orleans.Serialization/Buffers/Writer.FieldHeader.cs`
- `src/Orleans.Serialization/Codecs/ReferenceTypeSurrogateCodec.cs`

### 3.1 Orleans 的字段兼容，不靠成员顺序，靠 `Id`

`[GenerateSerializer]` 的类型里，真正稳的是 `[Id(x)]`。

成员顺序能变，成员名能变，字段还能搬位置，只要 `Id` 不变，老数据就还有机会读回来。

这就是 Orleans 为什么在序列化这件事上，一直强调“显式 id”。

### 3.2 `GenerateFieldIds` 是一个便利开关，不是兼容策略本身

`GenerateSerializerAttribute.GenerateFieldIds` 允许 Orleans 给一部分类型自动分配 field id，最常见的是 `PublicProperties`。

但它不是默认路径，默认还是 `None`。

源码里也能看出来，生成器对这件事很谨慎：

- `FieldIdAssignmentHelper` 会先找显式 `[Id]`
- 如果走隐式分配，会做哈希并检查冲突
- 如果发现 hash collision，会直接失败
- `GenerateSerializationAttributesAnalyzer` 会在类型有未标注成员时直接报错，除非你明确选了 `PublicProperties`

也就是说，Orleans 不是默认相信“反正可以自动推”。

它更倾向于逼你把 wire 结构写清楚。

### 3.3 `IdClashAttributeAnalyzer` 是最后一道静态门

`IdClashAttributeAnalyzer` 会在一个 `[GenerateSerializer]` 类型里找重复 `[Id]`。

这类错误不等到运行时才炸，而是编译期就拦掉。

这个选择挺对。

重复 `Id` 不是“小错误”，它会直接把 wire layout 搞乱，运行时再补救基本没戏。

### 3.4 `Writer.WriteFieldHeader(...)` 真正把 `Id` 落到 wire 上

写入字段头的时候，Orleans 不是只写一个数字就完了。

它还会同时决定：

- field id delta
- schema type
- 实际类型是不是 expected type
- 能不能用 well-known id
- 能不能复用引用类型表

这里最关键的是 `FieldHeaderCodec.ReadFieldHeader(...)` 和 `Writer.WriteFieldHeader(...)` 这一对。

它们把“字段号”和“字段类型信息”一起扛在了协议层里，所以老字段能跳过，新字段能加，顺序也能换。

### 3.5 容错反序列化的核心逻辑就是“读不懂就跳”

Orleans 的反序列化容错，不是神奇恢复，而是遵守两个规则：

- 认识的字段读，不认识的字段跳
- 能用已有类型表解析的类型就解析，解析不了就把错误控制在局部

像 `TypeSerializerCodec` 这种 codec，读取时会按 field id 找到已知槽位：

- `0` 读 schema type
- `1` 读 encoded type name
- `2` 读 well-known id 或 referenced id
- 其它字段直接 `ConsumeUnknownField(...)`

这就是为什么 field id 必须稳。

只要 field id 稳，老数据就能跳过新字段，新数据也能忽略老节点不认识的内容。

---

## 4. `alias` 负责的是名字稳定，不是字段稳定

关键文件：

- `src/Orleans.Serialization.Abstractions/Annotations.cs`
- `src/Orleans.Analyzers/GenerateAliasAttributesAnalyzer.cs`
- `src/Orleans.Analyzers/AliasClashAttributeAnalyzer.cs`
- `src/Orleans.Serialization/TypeSystem/RuntimeTypeNameFormatter.cs`
- `src/Orleans.Serialization/TypeSystem/TypeConverter.cs`
- `src/Orleans.CodeGenerator/CodeGenerator.cs`

### 4.1 `Alias` 能贴在类型，也能贴在方法上

这个属性很实用。

它的意思不是“好看一点”，而是“以后改名字，wire 还能认”。

对类型来说，alias 要全局唯一。

对方法来说，alias 只要在 declaring type 里唯一。

### 4.2 `GenerateAliasAttributesAnalyzer` 不是摆设

这个 analyzer 会逼着你给两类东西补 alias：

- 继承 Orleans grain interface 的 interface
- `GenerateSerializer` 的类型

它的思路很直接：

如果你的类型会出现在 wire 上，最好别完全依赖 CLR 全名。

因为全名最脆。

命名空间挪一下、嵌套层级改一下、类型重构一下，名字就变了。

### 4.3 `AliasClashAttributeAnalyzer` 会把冲突挡下来

别名不是越多越好。

一旦两个不同类型抢同一个 alias，或者同一个接口里的方法 alias 撞了，Orleans 会报错。

这也是兼容性里经常被忽略的一点：

稳定名字要靠统一管理，不是靠大家随手起。

### 4.4 `RuntimeTypeNameFormatter` 和 `TypeConverter` 会优先用 alias

`RuntimeTypeNameFormatter` 如果发现类型有 `CompoundTypeAliasAttribute`，会优先把它格式化成那个 alias。

`TypeConverter` 也会把 well-known alias、compound alias 和原始类型名互相映射起来。

这让类型名变成了一个可控协议，而不是纯 CLR 实现细节。

---

## 5. `TypeCodec` 不是 serializer，它是类型名协议的封装

关键文件：

- `src/Orleans.Serialization/TypeSystem/TypeCodec.cs`
- `src/Orleans.Serialization/TypeSystem/TypeConverter.cs`
- `src/Orleans.Serialization/Codecs/TypeSerializerCodec.cs`

### 5.1 `TypeConverter` 处理的是“名字怎么写、怎么读”

它负责：

- `Type -> string`
- `string -> Type`
- well-known alias
- compound alias
- 类型白名单和放行策略

它不直接找业务 codec。

它只是决定名字怎么进入 wire。

### 5.2 `TypeCodec` 负责把名字真正写成字节

`TypeCodec` 干的是：

- 对 `TypeConverter.Format(type)` 的结果做 UTF-8 编码
- 写长度前缀
- 写 hash
- 读回来以后做缓存命中和解析

这层很像一个小型名字协议。

它不是业务对象序列化，但它是业务对象序列化里的关键元件。

### 5.3 `TypeSerializerCodec` 是 `Type` 这个类型自己的 codec

`TypeSerializerCodec` 说明了一件事：

`Type` 本身也是可序列化对象，但它不是普通对象。

它会写：

- schema type
- encoded type name 或 well-known id
- reference tracking

读的时候会按 field id 顺序把它拼回来。

这也是 Orleans 一贯的风格：连反射里的 `Type` 都要进 wire 协议，而且不能乱来。

### 5.4 `SerializerTransparent` 的意思是“这个抽象类型别进协议层”

像 `CompatibilityStrategy`、`VersionSelectorStrategy` 这类基类都标了 `SerializerTransparent`。

意思不是“不能序列化”，而是“它们不该作为协议层里的真实类型节点”。

这类标记主要是在处理 inheritance 的边界时，避免协议被无意义的抽象基类污染。

---

## 6. 反序列化容错，靠的是几层兜底，不是单点补丁

关键文件：

- `src/Orleans.Serialization/Codecs/ReferenceTypeSurrogateCodec.cs`
- `src/Orleans.Serialization/Codecs/ReferenceCodec.cs`
- `src/Orleans.Serialization/Codecs/TypeSerializerCodec.cs`
- `src/Orleans.Serialization/Hosting/SerializerConfigurationAnalyzer.cs`
- `src/Orleans.Core/Configuration/Validators/SerializerConfigurationValidator.cs`

### 6.1 `ReferenceCodec` 负责对象图，不负责字段语义

对象图里如果有循环引用，`ReferenceCodec` 负责把对象先记下来，再在后面回填。

这对兼容性也有帮助，因为它让“同一个对象多次出现”不会被误解成多个对象。

### 6.2 `ReferenceTypeSurrogateCodec` 允许你把老类型换成 surrogate

这类 codec 是 Orleans 兼容性里很实用的逃生门。

当一个类型外部不能改、内部又想换表示方式时，可以：

- 用 surrogate 类型承接 wire
- 读回后 `ConvertFromSurrogate`
- 写出去前 `ConvertToSurrogate`

这比硬改原类型稳得多。

### 6.3 `SerializerConfigurationAnalyzer` 会在启动前先找出问题

它扫描 grain interface 的方法签名，看看参数和返回值有没有 serializer / copier。

开发环境下，`SerializerConfigurationValidator` 会默认打开这类检查。

这一步的意义很直接：

不要等请求跑到线上才发现某个类型压根不能序列化。

### 6.4 这层检查本身也有边界

它只能帮你找出“明显缺 codec / 缺 copier”的问题。

但它管不了这些更深的坑：

- 语义变了但字段 id 没变
- alias 改了但老节点还没更新
- 接口版本兼容方向错了
- 新旧节点对同一个方法理解不一样

所以它只是前置筛查，不是完整答案。

---

## 7. 代码生成层也在补兼容口子

关键文件：

- `src/Orleans.CodeGenerator/CodeGenerator.cs`
- `src/Orleans.CodeGenerator/SerializerGenerator.cs`
- `src/Orleans.CodeGenerator/InvokableGenerator.cs`

### 7.1 `GenerateCompatibilityInvokers` 是一个明显的后门

如果打开这个选项，Orleans 会为一些“具体实现类型”和“接口类型”再生成一份兼容 invoker。

这段逻辑的意思很实在：

如果方法已经从原始接口位置挪走了，或者实现类型和接口类型不完全一致，那就多补一份可识别的 invokable。

这就是编译期兼容补丁。

### 7.2 invokable 也有 alias，而且是 compound alias

`InvokableGenerator` 会给生成出来的 invokable 补 `CompoundTypeAliasAttribute`。

这个 alias 用来让 old / new 版本在类型名层面仍然能指到同一个 invokable 结构。

### 7.3 这说明 Orleans 不是只让 DTO 兼容，连方法调用载荷也在兼容

很多系统只关心数据结构升级。

Orleans 更麻烦一点，它连 RPC request 本身都要能跨版本活下来。

所以 codegen 这块不会只生成“最新最正确”的版本，它会留后手。

---

## 8. 旧新版本节点互通，卡的是接口版本，不是只有 serializer

关键文件：

- `src/Orleans.Core/Manifest/GrainVersionManifest.cs`
- `src/Orleans.Runtime/Versions/Selector/VersionDirectorManager.cs`
- `src/Orleans.Runtime/Versions/Compatibility/CompatibilityDirectorManager.cs`
- `src/Orleans.Runtime/Placement/PlacementService.cs`
- `src/Orleans.Runtime/Catalog/ActivationData.cs`
- `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`

### 8.1 先说清楚：这是接口版本，不是序列化版本

Orleans 的 grain interface 还有一层版本体系：

- `GrainVersionManifest` 记录本地和集群里有哪些版本
- `VersionSelectorManager` 决定请求该偏向哪些版本
- `CompatibilityDirectorManager` 决定这些版本是不是互相兼容

默认策略是：

- 兼容性：`BackwardCompatible`
- 版本选择：`AllCompatibleVersions`

也就是说，默认倾向于“老请求能往新实现上走”，但具体能不能走，还得看策略。

### 8.2 compatibility director 说的是“能不能兼容”

`ICompatibilityDirector` 只问一件事：

> 请求版本和当前版本是不是兼容？

默认的三个 director 很直白：

- `AllVersionsCompatibilityDirector`：全都兼容
- `BackwardCompatilityDirector`：请求版本 <= 当前版本
- `StrictVersionCompatibilityDirector`：必须完全相等

### 8.3 version selector 说的是“从可用版本里挑哪些”

`IVersionSelector` 会结合 compatibility director，从可用版本里挑出本次可接受的版本。

常见策略也是三种：

- `AllCompatibleVersionsSelector`
- `LatestVersionSelector`
- `MinimumVersionSelector`

这一步会和 `GrainVersionManifest` 配合，算出哪些 silo 对当前请求是合格的。

### 8.4 `ActivationData` 会在消息进入时直接判版本

这段最重要。

`ActivationData` 在处理消息时，如果发现 `message.InterfaceVersion` 和当前 activation 的本地版本不兼容，就会：

1. 把当前地址加进 cache invalidation header
2. 触发 `DeactivateReasonCode.IncompatibleRequest`
3. 让这个 activation 退场

这说明 Orleans 对版本不兼容的处理，不是“尽量凑合跑”。

它更像是：

“这条请求不该再继续在这个 incarnation 上跑，赶紧换新实例。”

### 8.5 版本协商和目录、放置也绑在一起

`CachedVersionSelectorManager` 会把：

- grain type
- interface type
- requested version

一起拿去算合适的 silo。

`PlacementService` 也会用这套结果选路。

所以旧新节点共存的时候，不是“谁都能接谁的请求”。

它是按版本先筛，再按放置策略挑，最后才真的送过去。

---

## 9. 这块里我觉得不够干净的地方

这一章最明显的感觉是：Orleans 的兼容性太分散了。

### 9.1 字段兼容、类型兼容、接口兼容、节点兼容，分了四套机制

它们分别由不同文件、不同 analyzer、不同 runtime service 处理。

好处是灵活。

坏处是你很难一眼说清“系统里到底谁在管兼容”。

### 9.2 `CodecProvider` 还是太像总装配点

序列化、copier、activator、converter、specializable codec，全往这里汇。

这让兼容性最后都很依赖一个中心服务。

中心化能跑，但维护成本高。

### 9.3 兼容补丁有点多

像：

- alias
- compound alias
- compatibility invoker
- surrogate codec
- version selector
- compatibility director

每个都在补一个洞。

这些洞加起来其实就是 Orleans 历史包袱的全貌。

### 9.4 旧新节点互通，本质上不是“无缝”，而是“可控降级”

这点得说实话。

Orleans 不是让所有旧新版本自然共存，而是把不兼容边界尽量控制在可识别、可退场、可重路由的范围里。

这已经很强了，但也说明它并不轻。

---

## 10. 如果你要自己复刻，我会怎么拆

如果目标是“复刻一版更干净的”，我会把它拆成四层：

### 10.1 Schema Layer

只管：

- field id
- optional field
- unknown field skip
- surrogate mapping

### 10.2 Name Layer

只管：

- `alias`
- `compound alias`
- `Type <-> string`

### 10.3 Call Compatibility Layer

只管：

- method / invokable 兼容
- interface version
- version selector / compatibility policy

### 10.4 Cluster Compatibility Layer

只管：

- old/new node coexistence
- placement on compatible silo
- incompatible request forwarding

这样做的好处是，边界会比 Orleans 现在更清楚。

坏处是，你会重新做一遍很多基础设施。

这也是 Orleans 这套东西“重”的根本原因。

---

## 11. 推荐阅读顺序

如果想顺着这篇再往下啃，我建议按这个顺序：

1. [04-serialization-runtime-chain.md](/Users/zhaoyiqi/Code/orleans/docs/04-serialization-runtime-chain.md)
2. `src/Orleans.Serialization/TypeSystem/TypeConverter.cs`
3. `src/Orleans.Serialization/TypeSystem/TypeCodec.cs`
4. `src/Orleans.Serialization/Buffers/Writer.FieldHeader.cs`
5. `src/Orleans.Serialization/Codecs/TypeSerializerCodec.cs`
6. `src/Orleans.CodeGenerator/FieldIdAssignmentHelper.cs`
7. `src/Orleans.CodeGenerator/CodeGenerator.cs`
8. `src/Orleans.Runtime/Versions/Selector/VersionDirectorManager.cs`
9. `src/Orleans.Runtime/Versions/Compatibility/CompatibilityDirectorManager.cs`
10. `src/Orleans.Runtime/Catalog/ActivationData.cs`

---

## 12. 这一篇的落点

把这一篇压成一句话，就是：

Orleans 的兼容性不是某个 serializer 的附加功能，而是从字段 id、类型名、alias、invokable、接口版本一直铺到集群路由的一整套协议设计。

这套设计很重，但它换来的是一件很实在的事：

你可以放心改一些东西。

只是这个“放心”，不是免费的。

