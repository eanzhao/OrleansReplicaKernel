# 序列化适配器与外部 Codec 生态（第三十六篇）

这一篇我先把结论放前面：

- Orleans 里的这些“外部序列化包”，本质上分两类。
- 第一类是桥接层：`Orleans.Serialization.SystemTextJson`、`Orleans.Serialization.NewtonsoftJson`、`Orleans.Serialization.MessagePack`、`Orleans.Serialization.MemoryPack`。
- 第二类是第一等实现：`Orleans.Serialization.FSharp` 里的 F# 专用 codec，以及 `src/Serializers/Orleans.Serialization.Protobuf` 里那批 Protobuf 专用 codec。
- 它们最后都不是绕开 Orleans 主序列化链，而是通过 `CodecProvider`、`ITypeFilter`、`IGeneralizedCodec`、`IGeneralizedCopier`、`TypeManifestOptions.WellKnownTypeAliases` 接进去。
- 如果你要重建一版，我的建议是：核心序列化只保留 Orleans 自己的对象图和引用语义，外部 codec 只做能力插件，不要变成主干的一部分。

换句话说：

> 外部包解决的是“怎么跟别人的类型和格式互通”。
> Orleans 主序列化链解决的是“怎么把对象图、引用、类型、生命周期、版本这些事讲清楚”。

这两件事不能混成一锅。

---

## 1. 先看横向差异

### 1.1 `SystemTextJson`

它解决的是最常见的一类互通问题：让 Orleans 能直接用 `System.Text.Json` 这一套生态。

它的特点是：

- 支持 `JsonElement`、`JsonDocument`、`JsonNode`、`JsonArray`、`JsonObject`、`JsonValue` 这些原生 JSON 类型。
- 通过 `JsonSerializerOptions`、`JsonReaderOptions`、`JsonWriterOptions` 让你控制 JSON 行为。
- 允许你用 `isSerializable` / `isCopyable` 去指定哪些业务类型走这条 codec。

它不是“替代 Orleans 自己的 serializer”，而是一个 JSON 适配层。

### 1.2 `NewtonsoftJson`

它解决的是另一类现实需求：老项目、动态 JSON、`JToken` 家族。

它和 STJ 很像，但支持面更偏向：

- `JObject`
- `JArray`
- `JProperty`
- `JValue`
- `JRaw`
- `JConstructor`

它用的是 `JsonSerializerSettings`，不是 STJ 的 options。
这意味着它更像“兼容老世界”的桥。

### 1.3 `MessagePack`

它解决的是二进制格式互通。

它的核心区别有两个：

- 既可以按 `MessagePackObjectAttribute` 识别类型，也可以允许 `DataContractAttribute`。
- 它是围绕 `MessagePackSerializerOptions` 来工作的。

源码里还直接写了一个很明确的提醒：它在性能上并不一定比 Orleans 默认 serializer 更好。
所以它的定位不是“更强的主序列化器”，而是“接入 MessagePack 生态的桥接 codec”。

### 1.4 `MemoryPack`

它也是二进制适配层，但支持的是 MemoryPack 生态。

它识别的核心类型特征是 `MemoryPackableAttribute`。

跟 MessagePack 一样，它的角色更像外部格式接入点，而不是 Orleans 内核的替身。

### 1.5 `FSharp`

F# 这块其实不是“外部 serializer bridge”，而是 Orleans 的语言适配层。

它直接给 F# 的语言类型补了专用 codec：

- `Unit`
- `FSharpOption<T>`
- `FSharpValueOption<T>`
- `FSharpChoice<...>`
- `FSharpRef<T>`

这类东西不是拿一个外部库来兜底，而是直接把 F# 语义翻成 Orleans 能理解的字段和 copier。
所以它更接近第一等实现，而不是桥。

### 1.6 `Serializers` / Protobuf

`src/Serializers/Orleans.Serialization.Protobuf` 这一支比较特别。

它一边是 Protobuf 消息的桥接层，用 `Google.Protobuf` 的 `IMessage` / `MessageParser` 来处理消息体；
另一边又给 `ByteString`、`MapField<TKey, TValue>`、`RepeatedField<T>` 这类类型写了专门 codec。

所以它不能简单归成一类：

- 对 `IMessage` 来说，它是桥。
- 对 Protobuf 运行时类型来说，它又是第一等 codec。

---

## 2. 它们怎么接进主序列化链

这部分是关键。

这些包看起来各自不同，但接入方式其实非常统一。

### 2.1 都从 `ISerializerBuilder` 入口挂进去

每个 bridge 包都有一个类似的方法：

- `AddJsonSerializer(...)`
- `AddNewtonsoftJsonSerializer(...)`
- `AddMessagePackSerializer(...)`
- `AddMemoryPackSerializer(...)`
- `AddProtobufSerializer(...)`

这一步不是“单纯加一个 NuGet 包”，而是在 Orleans 的 serializer builder 上登记一组服务和类型映射。

### 2.2 都注册成 `IGeneralizedCodec` / `IGeneralizedCopier` / `ITypeFilter`

它们大多都会做这几件事：

- `services.AddSingleton<TCodec>()`
- `services.AddFromExisting<IGeneralizedCodec, TCodec>()`
- `services.AddFromExisting<IGeneralizedCopier, TCodec>()`
- `services.AddFromExisting<ITypeFilter, TCodec>()`

这就是它们真正进入主链的方式。

不是独立跑一套序列化系统，而是成为 `CodecProvider` 能挑到的实现。

### 2.3 都通过 alias 进入 `TypeManifest`

这几类 codec 都会有自己的 `WellKnownAlias`，比如：

- `json`
- `json.net`
- `msgpack`
- `memorypack`
- `protobuf`

然后在 builder 里写进：

- `TypeManifestOptions.WellKnownTypeAliases[alias] = typeof(TheCodec)`

这一步很重要。

它把“类型别名”变成了运行时可识别的东西，也让生成出来的 manifest 能稳定引用这些 codec。

### 2.4 都按 Orleans 的 envelope 走，不直接裸写外部格式

这点特别值得注意。

这些 codec 并不是直接把外部 payload 塞到网络上就完事了，它们还是遵守 Orleans 的 field / reference / object envelope 规范。

典型结构是：

- 先看 `ReferenceCodec`
- 再写 codec 自己的 schema type
- 然后写原值的 `Type`
- 最后写外部 serializer 的 payload

也就是说：

> 外部 serializer 只负责“payload 长什么样”。
> Orleans 还是负责“这个对象在系统里怎么被标识、怎么被引用、怎么被回读”。

这就是桥接层的本质。

---

## 3. 共享骨架是什么

如果把这几类包的源码放在一起看，会发现它们长得非常像。

### 3.1 统一的注册骨架

几乎都是这个模式：

1. 提供一个 `Add*Serializer(...)` 扩展方法。
2. 提供一个 options 类。
3. 提供一个 codec 类型。
4. 注册 selector。
5. 把 codec 挂进 `IGeneralizedCodec`、`IGeneralizedCopier`、`ITypeFilter`。
6. 在 `WellKnownTypeAliases` 里登记别名。

这套骨架说明一件事：

Orleans 对外部 serializer 的支持不是“随便加一个适配器接口”这么简单，而是直接把它纳入了自己的类型体系和发现体系。

### 3.2 统一的支持类型判定

这些 codec 都有一层类似的判断：

- 先排除框架抽象类型
- 再判断原生支持类型
- 再看 selector
- 再看 options 里的 delegate
- 最后看库自身的 contract 特征

比如：

- JSON 走的是 native JSON 类型 + selector + `IsSerializableType`
- MessagePack 走的是 `MessagePackObjectAttribute` / `DataContractAttribute`
- MemoryPack 走的是 `MemoryPackableAttribute`
- Protobuf 走的是 `IMessage` + `MessageParser`

这意味着它们的“支持类型边界”其实是三层的：

1. 库本身的能力。
2. Orleans 提供的 selector。
3. 你在 options 里额外塞进去的规则。

### 3.3 统一的 copier 逻辑

这些包不只做序列化，也做 copier。

这点很 Orleans。

因为 Orleans 不是单纯的 wire serializer，它还要服务于 activation 内部的对象复制、回调参数、缓存对象图这类场景。

所以每个 bridge codec 都要同时实现：

- `IGeneralizedCodec`
- `IGeneralizedCopier`

这就是为什么它们会重复出现“serialize / deserialize”和“deep copy”两条链。

---

## 4. 差异到底在哪里

### 4.1 JSON 两兄弟是“格式兼容层”

`SystemTextJson` 和 `NewtonsoftJson` 都是在解决 JSON 世界的兼容问题，但侧重点不同。

`SystemTextJson` 更偏现代 .NET 原生生态，支持原生 `JsonNode` 家族。

`NewtonsoftJson` 更偏历史兼容和动态 JSON 结构，支持 `JToken` 家族。

它们本质上都是桥，不是核。

### 4.2 MessagePack 和 MemoryPack 是“二进制格式桥”

这两者都走紧凑二进制格式，但它们的 contract 特征不同：

- MessagePack 更依赖 `MessagePackObjectAttribute`，也兼容 `DataContractAttribute`。
- MemoryPack 更依赖 `MemoryPackableAttribute`。

它们都不是 Orleans 的内部格式，它们只是 Orleans 里的外部格式入口。

### 4.3 FSharp 是“语言级适配”

F# 这部分跟上面四个完全不是一个思路。

它做的是：

- 把 `Option`、`Choice`、`Ref` 这些 F# 语义类型变成 Orleans 可读的字段结构。
- 通过 `RegisterSerializer`、`RegisterCopier`、`GenerateSerializer` 参与 Orleans 的生成和注册体系。

这类 codec 并不依赖某个外部 serializer 引擎来完成主体工作。
它更像 Orleans 的一种原生扩展类型支持。

### 4.4 Protobuf 是“消息桥 + 运行时类型适配”

Protobuf 包最像桥，但又不只是桥。

它对 `IMessage` 的处理是标准的外部库接入：

- 通过 `MessageParser`
- 通过 `Descriptor.Parser`
- 通过消息体的 `WriteTo` / `ParseFrom`

但它又为 `ByteString`、`RepeatedField<T>`、`MapField<TKey, TValue>` 这些类型提供了专门 codec。

所以 Protobuf 包的定位更像：

- 消息体部分是桥。
- 运行时类型部分是第一等实现。

---

## 5. 和 `TypeManifest`、`CodecProvider`、生成器是什么关系

这一段是把前面的链条串起来。

### 5.1 `CodecProvider` 是运行时分发点

不管是桥接层还是第一等 codec，最后都要进入 `CodecProvider` 这条链。

它负责根据类型找出：

- 谁来写
- 谁来读
- 谁来复制

外部 codec 并没有脱离这个分发器。

### 5.2 `TypeManifest` 是“编译期/运行期的共同语言”

`TypeManifestOptions` 里有：

- `WellKnownTypeAliases`
- 各种 serializer / copier / activator / converter 的映射信息

外部 codec 用 alias 进入 manifest 后，运行时就可以按统一方式定位它。

这也是为什么这些包都要写 `Alias("json")` 之类的标记。

它不是装饰品，是可发现性协议的一部分。

### 5.3 生成器更偏向“第一等实现”，不是桥接层本体

像 F# 这类包，很多东西会直接走 `[RegisterSerializer]`、`[RegisterCopier]`、`[GenerateSerializer]` 这一套。

这说明：

- 生成器擅长的是“把具体类型变成 Orleans 原生 codec”。
- 桥接层擅长的是“把外部 serializer 接进 Orleans 的 type system”。

两者都重要，但位置不同。

### 5.4 `TypeFilter` 是边界守门员

这些 codec 大多都实现了 `ITypeFilter`。

它的意义很现实：

- 告诉 Orleans 这类类型能不能走这个 codec。
- 避免错误的 codec 被拿去处理不该处理的类型。

换句话说，`ITypeFilter` 是“这个适配器是不是该上场”的闸门。

---

## 6. 哪些是桥接层，哪些是第一等实现

我直接给一个比较干脆的划分。

### 6.1 桥接层

这几类我会明确归为桥接层：

- `Orleans.Serialization.SystemTextJson`
- `Orleans.Serialization.NewtonsoftJson`
- `Orleans.Serialization.MessagePack`
- `Orleans.Serialization.MemoryPack`
- `Orleans.Serialization.Protobuf` 里的 `IMessage` 适配部分

它们的共同点是：

- 依赖外部 serializer 或消息库。
- 目标是互通，不是重建 Orleans 自己的格式。
- 通过 Orleans 的 generalized codec 入口接入主链。

### 6.2 第一等实现

这几类我会明确归为第一等实现：

- `Orleans.Serialization.FSharp`
- `Orleans.Serialization.Protobuf` 里针对 `ByteString`、`MapField`、`RepeatedField` 的专用 codec

它们的共同点是：

- 直接表达某个语言或库的核心类型语义。
- 不靠一层外部通用 serializer 去“兜”。
- 更像 Orleans 自己定义的原生扩展类型支持。

---

## 7. 如果你要重建一版，我会怎么做

这是这篇最重要的落点。

### 7.1 核心序列化只保留一条主线

我会把核心序列化做得更硬一点：

- Orleans 自己的对象图格式只保留一套。
- 引用、循环、类型、字段、版本，都由核心 serializer 统一负责。
- 不要让外部 codec 自己发明一套“半独立协议”。

### 7.2 外部 codec 全部做成 capability module

JSON、MessagePack、MemoryPack、Protobuf 这类能力都应该是：

- 显式安装
- 显式注册
- 显式声明支持类型
- 显式进入 manifest

不要靠运行时扫描到处猜。

### 7.3 少重复三套相似的支持规则

现在这些包都在重复做下面三件事：

- selector
- options delegate
- native contract check

新系统里我会把它收敛成一个统一的 capability descriptor。

比如一个 codec 只需要告诉系统：

- 它支持什么 contract
- 它如何 deep copy
- 它如何和 manifest 对接

不要让每个包都自己拼一遍套路。

### 7.4 把 bridge 和 native codec 分层

这个一定要分清。

桥接层只负责外部生态互通。
第一等 codec 负责语言级和运行时级类型语义。

它们都可以进同一个 provider，但不能在抽象上混为一谈。

### 7.5 对性能别预设神话

源码里其实已经给了你一个很老实的信号：

外部 serializer 不一定比 Orleans 默认 serializer 更快。

所以如果你重建一版，不要默认“接上 MessagePack / MemoryPack 就更强”。
真正该优化的是：

- 类型发现成本
- 对象图 envelope
- 复制路径
- 代码生成和 manifest 查表成本

这比换一个外部 payload 格式更重要。

---

## 8. 推荐阅读顺序

如果你是按“复刻一版 Orleans”的思路往下看，我建议顺序是：

1. [03-codegen-proxy-invokable-serializer.md](/Users/zhaoyiqi/Code/orleans/docs/03-codegen-proxy-invokable-serializer.md)
2. [04-serialization-runtime-chain.md](/Users/zhaoyiqi/Code/orleans/docs/04-serialization-runtime-chain.md)
3. 这一篇
4. `src/Orleans.Serialization.SystemTextJson/SerializationHostingExtensions.cs`
5. `src/Orleans.Serialization.MessagePack/MessagePackCodec.cs`
6. `src/Orleans.Serialization.FSharp/FSharpCodecs.cs`
7. `src/Serializers/Orleans.Serialization.Protobuf/ProtobufCodec.cs`

这样看下来，你会很容易看清：

- 哪些是 Orleans 主链。
- 哪些是接生态的壳。
- 哪些是真正值得抽象成第一等能力的东西。

---

## 9. 最后一句

这批序列化适配器最有价值的地方，不是“让 Orleans 支持了更多库”，而是把一件事讲得很清楚：

> 一个运行时要做成可扩展平台，最重要的不是把所有东西揉成一个超大 serializer。
> 最重要的是把“核心语义”和“外部生态互通”分开。

Orleans 在这件事上已经走过很多弯路了。
你重建时，完全可以把这条边界画得比它更干净。
