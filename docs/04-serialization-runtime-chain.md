# 序列化运行时链路：CodecProvider、TypeConverter、MessageSerializer、GrainReferenceCodec 是怎么协同的（第四篇）

这一篇接第三篇。

第三篇讲的是：编译期把 `Codec_*`、`Copier_*`、`Activator_*`、`Metadata_*` 这些东西先生成出来。

这一篇要回答的是另一半：

这些生成物到了运行时以后，究竟是谁在做分发，谁在做类型名解析，谁在做消息封包，谁又在处理 Orleans 自己那套 Grain 引用。

先把结论压成几句直白的话：

- `CodecProvider` 是“类型到实现”的总路由器。要序列化某个 `Type`，先问它该用哪个 codec。
- `TypeConverter` 不负责找 codec。它负责把 CLR `Type` 变成可传输的名字，再从名字解析回 `Type`，顺手做别名和白名单校验。
- `TypeCodec` 是 `TypeConverter` 和二进制 wire format 中间的桥。它不决定策略，只负责把类型名真正写进字节流。
- `MessageSerializer` 负责一条 `Message` 在线上长什么样。消息头它自己手写，消息体才交给 codec 栈。
- `GrainReferenceCodec` 是 Orleans 的一个硬特例。Grain 引用不是按普通对象图序列化，而是降成 `(GrainId, GrainInterfaceType)`，读回来再重新造引用。

如果你脑子里先有这五个分工，后面的源码会顺很多。

---

## 1. 先把整条链压成一张图

```text
启动时
  生成代码里的 Metadata_* / TypeManifestProviderAttribute
    -> AddSerializer 扫描 ApplicationPart 程序集
    -> TypeManifestOptions
    -> CodecProvider / TypeConverter / TypeCodec / SerializerSessionPool

发送时
  Message.BodyObject
    -> CodecProvider.GetCodec(实际类型)
    -> 具体 codec.WriteField(...)
    -> Writer.WriteFieldHeader(...)
    -> 如果字段类型需要显式编码
         -> TypeCodec.WriteEncodedType(...)
         -> TypeConverter.Format(...)
    -> MessageSerializer.Write(...)
    -> 网络

接收时
  MessageSerializer.TryRead(...)
    -> 先读消息头
    -> 再读 body 第一个 field header
    -> 如果 field header 里带了类型信息
         -> TypeCodec.TryRead(...)
         -> TypeConverter.TryParse(...)
    -> CodecProvider.GetCodec(field.FieldType)
    -> 具体 codec.ReadValue(...)
    -> 如果字段是 GrainReference
         -> GrainReferenceCodec / TypedGrainReferenceCodec<T>
         -> IGrainFactory.GetGrain(...)
```

这张图里最容易看错的地方有两个。

第一，`MessageSerializer` 不是通用对象序列化器，它更像“消息帧编解码器”。它自己只管 `Message` 这一层。

第二，`TypeConverter` 不直接参与每个字段的读写分派。它真正参与的时机，是“字段头里需要把某个 CLR 类型编码进 wire format”的时候。

---

## 2. 第三篇是怎么交到这一篇手上的

第四篇的起点，其实是第三篇的终点。

关键文件：

- `src/Orleans.Serialization/Hosting/ServiceCollectionExtensions.cs`
- `src/Orleans.Serialization/Hosting/SerializerBuilderExtensions.cs`
- `src/Orleans.Serialization/Configuration/TypeManifestOptions.cs`
- `src/Orleans.Serialization/Internal/ReferencedAssemblyProvider.cs`

运行时启动时，`AddSerializer(...)` 会做几件事：

1. 用 `ReferencedAssemblyProvider.GetRelevantAssemblies()` 找出带 `ApplicationPartAttribute` 的相关程序集
2. 对每个程序集调用 `builder.AddAssembly(assembly)`
3. `AddAssembly(...)` 再去读这个程序集上的 `TypeManifestProviderAttribute`
4. 把这些 provider 注册成 `IConfigureOptions<TypeManifestOptions>`

最后，所有生成出来的 metadata 都会汇总进一个地方：

- `TypeManifestOptions`

这里面装的不是单一一种东西，而是一整套运行时要用的清单：

- `Serializers`
- `FieldCodecs`
- `Copiers`
- `Activators`
- `Converters`
- `InterfaceProxies`
- `Interfaces`
- `InterfaceImplementations`
- `WellKnownTypeIds`
- `WellKnownTypeAliases`
- `AllowedTypes`
- `CompoundTypeAliases`

然后两个核心运行时对象分别把这份清单吃进去：

- `CodecProvider` 关心“有哪些 serializer/copier/activator/converter”
- `TypeConverter` 关心“哪些类型名允许出现、有哪些 alias、有哪些 compound alias”

也就是说，编译期那份 `Metadata_*` 到运行时不是“变成注册代码”，而是“变成一份 manifest 配置”，然后由这几个基础服务再消费。

这点很重要。

Orleans 这套序列化不是“启动时扫类型然后现场反射推导”，而是“编译期先把路修好，运行时按图索骥”。

---

## 3. 四个核心角色，各自到底负责什么

先把边界说死，不然后面很容易混。

### 3.1 `CodecProvider`

关键文件：

- `src/Orleans.Serialization/Serializers/CodecProvider.cs`

它回答的问题是：

> 现在我要处理一个 `Type`，到底该用哪个 `IFieldCodec`、`IValueSerializer`、`IDeepCopier`、`IActivator`？

它会先吃 manifest 里的静态注册信息，然后在运行时按下面这套顺序找实现：

1. 精确命中的生成 codec
2. 泛型定义对应的 codec
3. 数组、枚举这类内建分支
4. 通过 `IConverter<,>` 生成 surrogate codec
5. `ISpecializableCodec`
6. `IGeneralizedCodec`
7. 如果是抽象类或接口，退回 `AbstractTypeSerializer`

所以它不是一个“纯字典”。

它更像一个总装配点。生成代码、内建 codec、special case、抽象类型兜底，全从这里汇合。

### 3.2 `TypeConverter`

关键文件：

- `src/Orleans.Serialization/TypeSystem/TypeConverter.cs`

它回答的问题是：

> 某个 CLR `Type` 该怎么写成字符串？收到字符串后又该怎么还原回 `Type`？

它做的事主要有四类：

- `Type -> string`
- `string -> Type`
- well-known alias 和 compound alias 重写
- `AllowedTypes`、`ITypeNameFilter`、`ITypeFilter` 的放行检查

这里有个很容易误会的点：

`TypeConverter` 不是 Orleans 路由层里那个“grain type / interface type 转换器”。它处理的是 CLR 类型名，不是 `GrainType`、`GrainInterfaceType` 这种 Orleans 自己的 ID。

### 3.3 `MessageSerializer`

关键文件：

- `src/Orleans.Core/Messaging/MessageSerializer.cs`

它回答的问题是：

> 一条 `Message` 在线上的二进制布局是什么？

它管三件事：

- 帧头两个长度字段怎么写
- `Message` 自己的 header 字段怎么写
- body 什么时候交给 codec 栈，以及读回来的时候怎么还原

它不负责理解业务对象，只负责把“消息这层壳”包起来和拆开。

### 3.4 `GrainReferenceCodec`

关键文件：

- `src/Orleans.Core.Abstractions/Runtime/GrainReference.cs`

它回答的问题是：

> 如果字段里出现的是一个 Grain 引用，该怎么序列化？

答案不是“把代理对象整个写进去”，而是：

1. 取出 `GrainId`
2. 取出 `GrainInterfaceType`
3. 写成 `GrainReferenceSurrogate`
4. 读回来以后调用 `IGrainFactory.GetGrain(...)` 重新拿引用

这就是 Orleans 处理远程对象引用的基本立场：

传的是身份，不是对象状态。

---

## 4. 发送路径：一条消息是怎么被写出去的

这一段从 `MessageSerializer.Write(...)` 开始看。

关键文件：

- `src/Orleans.Core/Messaging/MessageSerializer.cs`
- `src/Orleans.Serialization/Buffers/Writer.FieldHeader.cs`
- `src/Orleans.Serialization/Codecs/ObjectCodec.cs`
- `src/Orleans.Serialization/Session/SerializerSession.cs`

### 4.1 第一步：先决定 body 用哪个 codec

`MessageSerializer.Write(...)` 一进来，会先看：

```csharp
message.BodyObject
```

只要 body 不为空，它就直接按运行时实际类型找 codec：

```csharp
bodyCodec = _codecProvider.GetCodec(message.BodyObject.GetType())
```

这里有个很现实的含义：

`MessageSerializer` 对 body 的结构一无所知。它不管这是 `Invokable_*`、`Response<T>`、异常对象，还是别的什么。它只负责把实际类型交给 `CodecProvider`。

也就是说，body 的“业务语义”完全下沉到 codec 栈里了。

### 4.2 第二步：响应对象有一条特殊优化路径

`MessageSerializer` 里有一段容易漏看的逻辑：

- 如果 `message.BodyObject` 的 codec 同时也是 `ResponseCodec`
- 并且 `headers.ResponseType` 还是 `None`

它会走 raw response 的快捷路径。

具体做法是：

1. 把 `headers.ResponseType` 改成 `Success`
2. 不再按 `Response<T>` 的完整外壳写类型
3. 直接把里面那个简单结果值按 raw 方式写出去

对应代码在：

- `src/Orleans.Serialization/Invocation/Response.cs`

这条优化的目的很直接：

普通成功返回值太常见了，没必要每次都完整包一层 `Response<T>` 再上网。

所以这里其实已经能看出一个 Orleans 风格：性能热点会直接在消息层做专门分叉，而不是硬坚持一套绝对统一的抽象。

### 4.3 第三步：消息头是手写的，不走通用 codec 栈

`MessageSerializer.Serialize(...)` 对 header 的处理非常直接，就是自己一项一项写：

- `PackedHeaders`
- `CorrelationId`
- `SendingGrain`
- `TargetGrain`
- `SendingSilo`
- `TargetSilo`
- TTL
- `InterfaceType`
- `InterfaceVersion`
- cache invalidation header
- request context

这里面你会看到几套不同风格的编码方式混在一起：

- `GrainId` 用 `IdSpan` 相关 codec 手写
- `SiloAddress` 用缓存 codec 手写
- `InterfaceType` 直接写 `IdSpan`
- `RequestContext` 又是单独的手写格式

这跟 body 的风格完全不一样。

body 走的是“按 `Type` 找 codec，再交给 codec 递归写字段”；header 则是“`MessageSerializer` 自己知道每个字段长什么样，自己写”。

这就是 Orleans 这里第一层不够整齐的地方。

它不是坏实现，但确实不是一套纯统一的序列化模型。

### 4.4 `RequestContext` 还是手写的，而且比表面更手写

`MessageSerializer` 构造函数里会注入：

```csharp
DictionaryCodec<string, object>
```

但是实际写 `RequestContext` 的时候，用的是它自己那套：

- 先写字典大小
- key 用 `WriteString(...)`
- value 用 `ObjectCodec.WriteField(...)`

读的时候也是反过来手写读。

也就是说，这里并没有真的把整个 `Dictionary<string, object>` 交给 `DictionaryCodec<string, object>`。

这在架构上是个很典型的信号：

Orleans 的消息层虽然借用了通用 codec 栈，但它又保留了不少历史上手写的“消息格式特例”。

如果你以后要自己复刻，我会认真考虑把这块切清楚，要么全交给专门的 header codec，要么就明确承认它是独立协议，不要这种半接半不接的状态。

### 4.5 真正写字段时，`CodecProvider` 和 `TypeCodec` 怎么介入

当 body codec 开始写字段时，核心入口是：

- `Writer.WriteFieldHeader(...)`

这里会同时看三件事：

- `fieldId`
- `expectedType`
- `actualType`

如果 `actualType == expectedType`，那最省事，字段头只写 wire type 和 field id，不需要额外带类型信息。

如果两者不同，就要决定怎么表达“真实类型”。

`Writer.WriteFieldHeader(...)` 的策略是：

1. 能用 well-known type id 就写 `SchemaType.WellKnown`
2. 否则如果这个类型在当前 session 里已经出现过，就写 `SchemaType.Referenced`
3. 再不行，就写 `SchemaType.Encoded`

走 `Encoded` 的时候才会真正调用：

```csharp
Session.TypeCodec.WriteEncodedType(...)
```

而 `TypeCodec` 再往下会调用：

```csharp
TypeConverter.Format(type)
```

这就是这几个角色真正串起来的地方：

```text
具体 codec
  -> Writer.WriteFieldHeader
  -> TypeCodec
  -> TypeConverter
```

所以 `TypeConverter` 并不是“每个字段都会碰到”，而是只有字段头需要显式描述运行时真实类型时，它才会进场。

### 4.6 `SerializerSession` 在这里起什么作用

`MessageSerializer` 自己持有两份 session：

- 一份给写
- 一份给读

session 里最重要的几个东西是：

- `TypeCodec`
- `WellKnownTypes`
- `ReferencedTypes`
- `ReferencedObjects`
- `CodecProvider`

这里有个细节很值得记：

在 `MessageSerializer.Write(...)` 里，header 写完以后会先做一次：

```csharp
_serializationSession.PartialReset()
```

这个 `PartialReset()` 只清 `ReferencedObjects`，不清 `ReferencedTypes`。

也就是说：

- header 和 body 之间不共享对象引用编号
- 但仍然共享“这个消息里已经出现过哪些类型”的缓存

这说明 Orleans 在消息这一层，默认把“类型引用缓存”和“对象图引用缓存”看成两种不同性质的状态。

这个处理挺细，也挺绕。

---

## 5. 接收路径：字节流是怎么还原回对象的

关键文件：

- `src/Orleans.Core/Messaging/MessageSerializer.cs`
- `src/Orleans.Serialization/Buffers/Writer.FieldHeader.cs`
- `src/Orleans.Serialization/Codecs/ReferenceCodec.cs`
- `src/Orleans.Serialization/Codecs/ObjectCodec.cs`

### 5.1 先拆帧，再拆 header，再拆 body

`MessageSerializer.TryRead(...)` 的顺序很硬：

1. 先读固定 8 字节 framing
2. 得到 `headerLength` 和 `bodyLength`
3. 校验大小
4. 先反序列化 header
5. 再反序列化 body

这里 header 和 body 是完全分开的两段。

而且代码里还明确写了一个意图：

> body 反序列化比 header 更容易失败，所以要分开处理

这很合理。消息头错了通常是协议层坏了；body 错了很多时候只是某个类型没法解、版本对不上，或者业务对象不兼容。

### 5.2 body 读回来的第一步，是先读 field header

`ReadBodyObject(...)` 一开始先读：

```csharp
var field = reader.ReadFieldHeader();
```

这个 field header 里面，已经带了后面选 codec 所需的大部分信息：

- wire type
- field id
- 是否 reference
- 如果需要的话，还带运行时字段类型

一旦拿到 `field.FieldType`，后面就顺了：

- 普通路径：`_codecProvider.GetCodec(field.FieldType)`
- 成功响应快捷路径：根据 `field.FieldType` 构造 `Response<T>` 对应的 raw codec

### 5.3 字段头里的类型是怎么读出来的

字段头的读取逻辑在：

- `FieldHeaderCodec.ReadFieldHeader(...)`

如果 schema type 不是 `Expected`，它会继续读类型：

- `WellKnown` -> 去 `WellKnownTypeCollection` 查
- `Referenced` -> 去 `ReferencedTypes` 查
- `Encoded` -> 调 `reader.Session.TypeCodec.TryRead(...)`

而 `TypeCodec.TryRead(...)` 再往下做的是：

1. 读版本号
2. 读 hash
3. 读 UTF-8 类型名字节串
4. 用缓存命中已有 `Type`
5. 没命中就把字节串转成字符串
6. 调 `TypeConverter.TryParse(...)`

也就是说，接收路径上 `TypeConverter` 真正出场的地方，是：

> 从 wire format 里那串类型名，恢复出一个 CLR `Type`

### 5.4 `CodecProvider` 在读路径上的角色和写路径一样重

很多人第一次看会下意识觉得：

- 写的时候要找 codec
- 读的时候反正 type 已经有了，应该简单点

实际上并没有简单多少。

读的时候同样是：

```csharp
var bodyCodec = _codecProvider.GetCodec(field.FieldType);
return bodyCodec.ReadValue(...)
```

因为“知道目标类型”不等于“知道该用哪个实现去读”。

比如这个 `Type` 可能对应的是：

- 生成的 `Codec_*`
- 数组 codec
- surrogate codec
- grain reference special codec
- generalized codec
- 抽象类型 serializer

这些分派仍然都要靠 `CodecProvider`。

### 5.5 对象引用和循环图还是靠 `ReferenceCodec`

只要字段是引用类型，底下就会碰到：

- `ReferenceCodec.TryWriteReferenceField(...)`
- `ReferenceCodec.ReadReference(...)`

`ReferenceCodec` 负责的不是“序列化对象内容”，而是对象身份追踪：

- 这个对象之前写过没有
- 这个 reference id 指向哪个对象
- 如果出现前向引用，先放 placeholder，之后再回填

它和 `CodecProvider` 的关系可以简单理解成：

- `CodecProvider` 负责找“怎么序列化这个类型”
- `ReferenceCodec` 负责管“这个对象是不是已经出现过”

这两层是叠在一起的，不是二选一。

---

## 6. `GrainReferenceCodec`：为什么 Grain 引用必须单独对待

这一段是 Orleans 序列化里最有“框架味”的地方。

关键文件：

- `src/Orleans.Core.Abstractions/Runtime/GrainReference.cs`
- `src/Orleans.Serialization/Codecs/GeneralizedReferenceTypeSurrogateCodec.cs`
- `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs`
- `src/Orleans.Core/Core/DefaultClientServices.cs`

### 6.1 Grain 引用不是普通对象

一个 `IUserGrain` 变量在运行时看起来像普通接口，但它背后其实是一个代理引用。

这个东西如果按普通对象图去序列化，会立刻出几个问题：

- 代理对象本身没意义
- 里面挂着运行时服务、共享状态、拷贝池之类的东西
- 你真正想传的不是“这个代理长什么样”，而是“它指向哪个 grain”

所以 Orleans 这里直接定了另一条语义：

> Grain 引用只传身份，不传内部结构。

### 6.2 它是怎么接进 `CodecProvider` 的

`GrainReferenceCodecProvider` 实现的是：

- `ISpecializableCodec`

它声明自己支持：

```csharp
typeof(IAddressable).IsAssignableFrom(type)
```

所以当 `CodecProvider` 发现某个字段类型没有直接命中的生成 codec 时，会继续扫 specializable codecs。只要类型是 `IAddressable`，这里就能接住。

然后它会返回：

- `TypedGrainReferenceCodec<T>`

这意味着 Grain 引用这条 special case，不是靠 manifest 明写进去的，而是靠运行时服务注册插进来的。

这也是 Orleans 这里第二个不那么干净的点：

你如果只看编译期 metadata，会以为序列化表已经闭环了；但真正跑起来以后，仍然有一层运行时注入的特殊分支。

### 6.3 `TypedGrainReferenceCodec<T>` 实际写了什么

`TypedGrainReferenceCodec<T>` 继承的是：

- `GeneralizedReferenceTypeSurrogateCodec<T, GrainReferenceSurrogate>`

这套基类已经把大框架搭好了：

1. 如果对象之前写过，先走 `ReferenceCodec`
2. 否则写一个 tag-delimited object
3. 把真实值转成 surrogate
4. 用 surrogate 的 serializer 去写

而 `GrainReferenceSurrogate` 本身很小，只有两个字段：

- `GrainId`
- `GrainInterfaceType`

所以 Grain 引用上网时，真正传的就这两个东西。

### 6.4 读回来时为什么能重新变成代理

反序列化时，`ConvertFromSurrogate(...)` 干的事很直白：

```csharp
_grainFactory.GetGrain(surrogate.GrainId, surrogate.GrainInterfaceType)
```

也就是说，收到一个 grain 引用，不是“还原原对象”，而是“本地重新拿一个等价引用”。

这跟 Orleans 的整体模型完全一致：

- Grain 本体不跨进程搬运
- 跨进程传播的是 identity 和调用能力

### 6.5 为什么还要有 `GrainReferenceCopier`

因为 Orleans 不光要序列化，还会深拷贝参数和状态。

对于 grain 引用，深拷贝其实没有意义，所以这里配的是浅拷贝语义：

- `GrainReferenceCopier : ShallowCopier<GrainReference>`
- `TypedGrainReferenceCopier<TInterface>`

这也是一个很能说明问题的点：

在 Orleans 眼里，grain reference 更像句柄，不像数据。

### 6.6 `IGrainObserver` 为什么要单独拦

代码里还专门拦了一个坑：

如果传进来的不是合法的 observer reference，而是某个普通 observer 实例，就直接抛异常。

异常信息也写得很直白：

> Did you forget to CreateObjectReference?

这说明 Orleans 在这里的约束很强：

可以传远程引用，但这个引用必须先被框架正规化，不能把任意本地对象冒充成 observer 发上网。

---

## 7. `TypeConverter` 的边界：它管什么，不管什么

这一段单独拎出来，是因为太容易脑补过头。

### 7.1 它管的是 CLR 类型名

比如：

- `List<OrderDto>`
- `Dictionary<string, object>`
- 某个生成的 `Invokable_*`
- surrogate 类型

这些类型如果需要出现在字段头里，最终都会经过：

- `TypeConverter.Format(...)`
- `TypeConverter.TryParse(...)`

### 7.2 它不管 Orleans 自己那套 ID

下面这些东西不走 `TypeConverter`：

- `GrainType`
- `GrainInterfaceType`
- `GrainId`
- `SiloAddress`

这些在 `MessageSerializer` 里要么手写，要么用 Orleans 自己的专用 codec。

所以如果你在读源码时发现：

- body 字段类型走 `TypeConverter`
- header 里的接口类型却没有走

别怀疑自己看漏了，设计上就是两套东西。

### 7.3 它还顺手承担了类型安全策略

`TypeConverter` 里除了 alias 和 parse/format，还有一整套放行检查：

- `AllowAllTypes`
- `AllowedTypes`
- `ITypeNameFilter`
- `ITypeFilter`

这意味着它不只是“名字转换器”，其实还是类型名进入系统时的第一道闸。

从职责上说，这已经有点重了。

如果你以后自己复刻，我会倾向把它拆成两层：

1. 纯类型名格式化与解析
2. 类型白名单与策略校验

现在 Orleans 这两个职责是揉在一起的。

---

## 8. 从这条链里能看到的几个架构问题

既然你一开始就觉得 Orleans 架构不够干净，这一篇里我觉得最值得记的有四个点。

### 8.1 `CodecProvider` 是个过重的总入口

它同时管：

- field codec
- value serializer
- deep copier
- base codec
- activator
- converter

这很方便，但中心化过头了。

一旦你想追某种类型为什么这样被处理，最后几乎都得回到 `CodecProvider`。

### 8.2 `MessageSerializer` 只把 body 交给通用栈，header 还是半手工协议

这会造成一种不一致：

- body 看起来很“类型驱动”
- header 看起来很“消息协议驱动”

两边都对，但放在一起时，边界不够漂亮。

### 8.3 `RequestContext` 是更明显的例外

它明明长得像一个普通字典，却没有完整走字典 codec，而是在消息层自己展开。

这会让“消息协议”和“对象序列化协议”之间出现一块模糊地带。

### 8.4 Grain 引用 special case 藏在运行时服务注册里

如果你只看生成代码和 manifest，很容易漏掉这件事。

真正的 Grain reference 支持，是靠：

- `DefaultClientServices`
- `DefaultSiloServices`

把 `ISpecializableCodec` 和 `ISpecializableCopier` 注册进去的。

这说明 Orleans 的序列化闭环并不是纯 compile-time 闭环，它还有一层运行时拼装。

---

## 9. 如果你要完整复刻，我会怎么拆这一块

如果你的目标是“完整复刻，但架构比 Orleans 更整齐”，我建议这一层至少拆成四块。

### 9.1 Manifest Registry

只负责：

- 吃编译期 metadata
- 暴露类型到 serializer/copier/activator 的静态映射

不要顺手负责动态 special case 和运行时 fallback。

### 9.2 Type Name System

只负责：

- `Type <-> WireTypeName`
- alias
- type policy / allow list

不要再和 codec 查找耦在一起。

### 9.3 Message Framing Layer

只负责：

- message framing
- header layout
- body 边界

header 如果要自己定协议，就彻底独立；如果不想独立，就让 header 也走一个明确的 header codec。

### 9.4 Domain Special Codecs

像 Grain reference、stream identity、observer reference 这种强领域对象，明确放到单独层里，不要混在“普通对象序列化”语义里假装一样。

这样做的好处是：

- 普通数据对象链路更纯
- Orleans 特有语义集中
- 后面做兼容层、协议演进、调试工具都更容易

---

## 10. 这一篇最该反复看的源码文件

建议按这个顺序读：

1. `src/Orleans.Core/Messaging/MessageSerializer.cs`
2. `src/Orleans.Serialization/Serializers/CodecProvider.cs`
3. `src/Orleans.Serialization/Buffers/Writer.FieldHeader.cs`
4. `src/Orleans.Serialization/TypeSystem/TypeCodec.cs`
5. `src/Orleans.Serialization/TypeSystem/TypeConverter.cs`
6. `src/Orleans.Serialization/Codecs/ObjectCodec.cs`
7. `src/Orleans.Serialization/Codecs/ReferenceCodec.cs`
8. `src/Orleans.Serialization/Invocation/Response.cs`
9. `src/Orleans.Core.Abstractions/Runtime/GrainReference.cs`
10. `src/Orleans.Serialization/Hosting/ServiceCollectionExtensions.cs`
11. `src/Orleans.Serialization/Hosting/SerializerBuilderExtensions.cs`
12. `src/Orleans.Serialization/Internal/ReferencedAssemblyProvider.cs`

如果你把这 12 个文件顺下来，再回头看第二篇里的“请求怎么从一端跑到另一端”，很多原来像黑盒的地方就会突然透明很多。

---

## 11. 这一篇的落点

把这一篇压成一句话，就是：

Orleans 运行时的序列化链路，不是一个“统一的大 serializer”在工作，而是四层东西叠在一起：

- `MessageSerializer` 管消息壳
- `CodecProvider` 管类型分发
- `TypeConverter`/`TypeCodec` 管类型名进出 wire format
- `GrainReferenceCodec` 负责 Orleans 特有的远程引用语义

这四层能配合起来，靠的是第三篇那套编译期 metadata 闭环；而它们看起来没那么干净，也正是因为 Orleans 在这条线上同时追求了三件事：

- 强类型
- 高性能
- 历史兼容

通常这三件事凑在一起，代码就很难真的清爽。

下一篇如果继续往下写，我建议直接接：

- `ActivationData` 为什么会成为 Orleans 复杂度中心

或者换个方向，补一篇：

- Orleans 的类型系统和 ID 系统：`GrainType`、`GrainInterfaceType`、`TypeConverter`、generic grain 是怎么接起来的

