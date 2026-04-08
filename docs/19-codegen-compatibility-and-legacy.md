# 代码生成兼容层与历史包袱：Orleans 的 codegen 为什么不只是“新链路”（第十九篇）

这一篇只讲一件事：Orleans 的 codegen 不是单纯“生成新代码”，它同时还在照顾旧 serializer、旧 invoker、旧类型名、旧 grain id，以及这些老东西和新链路之间怎么继续互认。

先把结论说清楚：

- Orleans 的 codegen 不是一条纯新的流水线，而是一层兼容层。
- `GenerateCompatibilityInvokers` 会额外生成兼容 invoker，老方法名还能继续被认出来。
- `SerializerTransparent`、`DefaultInvokableBaseType`、`ReturnValueProxy`、`InvokableBaseType`、`InvokableCustomInitializer` 这些属性，都是给历史包袱留的口子。
- `[Serializable]`、`ISerializable`、`Exception` 这些旧序列化形态没有被扔掉，还是有专门的 codec 和 analyzer 在兜着。
- `LegacyGrainId`、`GrainTypePrefix`、`DotNetSerializableCodec` 这种老路径，今天还在运行时里活着。

如果把这一篇压成一句话，就是：

> Orleans 不是“新实现把旧实现换掉”，而是“新实现先站稳，再把旧实现一层层包进去”。

---

## 1. 先把整条兼容链压成一张图

```text
用户源码
  -> [GenerateSerializer] / [GenerateMethodSerializers]
  -> [Serializable] / Orleans analyzer
  -> [Alias] / [Id] / [CompoundTypeAlias]
  -> [SerializerTransparent] / [DefaultInvokableBaseType] / [ReturnValueProxy]
  -> [InvokableBaseType] / [InvokableCustomInitializer] / [InvokeMethodName]
  -> source generator 读取 build property
  -> CodeGenerator 扫描 compilation
  -> 生成 Codec_* / Proxy_* / Invokable_* / Metadata_*
  -> 如果打开 GenerateCompatibilityInvokers，再额外生成一份兼容 invoker
  -> 生成 assembly-level ApplicationPart / TypeManifestProvider
  -> 运行时 AddAssembly() / AddSerializer() 吃进 TypeManifestOptions
  -> CodecProvider / TypeConverter / RuntimeClient / GrainReferenceActivator 消费这些清单
  -> 老 serializer / 老 invoker / 老 type name 继续被识别
```

这张图里最关键的点是：

Orleans 没有把兼容逻辑塞进一个单独的“老版本模块”里，而是拆进了生成器、manifest、runtime codec、属性系统和 analyzer 里。

所以它的历史包袱不是一块砖，而是整条链都沾着。

---

## 2. 编译期这边，兼容是怎么被塞进去的

关键文件：

- `src/Orleans.CodeGenerator/OrleansSourceGenerator.cs`
- `src/Orleans.CodeGenerator/CodeGenerator.cs`
- `src/Orleans.CodeGenerator/InvokableGenerator.cs`
- `src/Orleans.CodeGenerator/SerializerGenerator.cs`
- `src/Orleans.CodeGenerator/MetadataGenerator.cs`
- `src/Orleans.CodeGenerator/ApplicationPartAttributeGenerator.cs`

### 2.1 source generator 先把开关读进来

`OrleansSerializationSourceGenerator` 会从 MSBuild 里读一个开关：

```csharp
options.GenerateCompatibilityInvokers
```

它不是摆设。这个开关直接决定 codegen 要不要为旧接口形态再吐一份 invoker。

### 2.2 `CodeGenerator` 不是只生成“现在这一版”的东西

`CodeGenerator` 在生成 invokable 时，除了正常生成当前接口对应的 `Invokable_*`，还会在兼容模式打开时补一条分支：

```csharp
if (Options.GenerateCompatibilityInvokers && !SymbolEqualityComparer.Default.Equals(method.OriginalDefinition.ContainingType, interfaceType))
```

这句说明两件事：

1. 兼容 invoker 不是默认产物，是显式打开的额外产物。
2. 只要触发了这条分支，生成器就会再生成一份 `Invokable_*`，再补一条 `CompoundTypeAlias`，让运行时还认得老名字。

### 2.3 `MetadataGenerator` 把兼容产物送回运行时

兼容 invoker 生成出来以后，还要进入 manifest。

`MetadataGenerator` 会把这些东西写进 `TypeManifestOptions`：

- `Serializers`
- `Copiers`
- `Converters`
- `InterfaceProxies`
- `Interfaces`
- `InterfaceImplementations`
- `Activators`
- `WellKnownTypeIds`
- `WellKnownTypeAliases`
- `CompoundTypeAliases`

兼容 invoker 的关键不是“类生成出来了”，而是“类名被塞进了类型名解析树里”。

这里最值得注意的是 `CompoundTypeAliases`。它不是普通字典，更像一棵前缀树。Orleans 甚至允许同一个接口在多个程序集里各自生成一份，启动时再把这些清单合并起来，先到的那份优先保留。兼容层里很多边角问题，最后都会卡在这种“名字撞车以后谁说了算”的地方。

### 2.4 `ApplicationPart` 和 `TypeManifestProviderAttribute` 是一对

`ApplicationPartAttributeGenerator` 会给程序集补一个 assembly-level attribute。

这不是为了装饰，而是为了让运行时还能把这些生成产物找回来。

而 `TypeManifestProviderAttribute` 才是另一半。它把生成出来的 `Metadata_*` 再挂回运行时配置系统，让 `TypeManifestProviderBase` 和 `TypeManifestOptions` 真正把这些清单吃进去。

少了 `ApplicationPart`，运行时找不回这些程序集。
少了 `TypeManifestProviderAttribute`，找回来也不知道该怎么注册。

---

## 3. 旧 invoker 兼容：为什么 Orleans 还要多生成一份

关键文件：

- `src/Orleans.Core.Abstractions/Runtime/IAddressable.cs`
- `src/Orleans.Core.Abstractions/Runtime/IGrainExtension.cs`
- `src/Orleans.Core.Abstractions/Runtime/GrainReference.cs`
- `src/Orleans.Core.Abstractions/Runtime/AsyncEnumerableRequest.cs`
- `src/Orleans.CodeGenerator/InvokableGenerator.cs`
- `src/Orleans.CodeGenerator/Model/InvokableMethodDescription.cs`
- `src/Orleans.CodeGenerator/Model/ProxyMethodDescription.cs`

### 3.1 `GenerateMethodSerializers` 还是老入口

`IAddressable` 和 `IGrainExtension` 现在仍然带着：

- `[GenerateMethodSerializers(typeof(GrainReference))]`
- `[GenerateMethodSerializers(typeof(GrainReference), isExtension: true)]`

这说明 Orleans 并不是只围着新接口模型转，它还是把地址型接口和扩展接口当成 codegen 的根入口。

### 3.2 `DefaultInvokableBaseType` 是很典型的历史包袱

`GrainReference` 上挂着一串默认返回类型映射：

- `ValueTask<> -> Request<>`
- `ValueTask -> Request`
- `Task<> -> TaskRequest<>`
- `Task -> TaskRequest`
- `void -> VoidRequest`
- `IAsyncEnumerable<> -> AsyncEnumerableRequest<>`

这串映射不是语法糖，它是在告诉生成器：

“你看到这些返回类型时，不要直接自己拼一个最朴素的 request 类型，要走我们这套默认兼容基类。”

这也是 Orleans 的味道：它不是删掉旧类，而是把旧类固化成默认底座。

### 3.3 `ReturnValueProxy` 让返回值对象自己参与初始化

`AsyncEnumerableRequest<T>` 上有：

- `[ReturnValueProxy(nameof(InitializeRequest))]`

这说明返回值类不是纯数据包，它还要被生成器当成一个可以自举的中间对象来用。

这条路径看起来绕，但它正好支撑了 `IAsyncEnumerable<T>` 这类特殊返回值：

- 先造 request
- 再把 `GrainReference`、返回值包装、枚举器代理接起来

### 3.4 `InvokableBaseType` 和 `InvokableCustomInitializer` 是更细的兼容口子

`InvokableMethodDescription` 会从方法属性里读：

- `InvokableBaseTypeAttribute`
- `InvokableCustomInitializerAttribute`
- `ResponseTimeoutAttribute`

这表示方法级别不是只能“按默认模板生成”。

如果老接口需要不同的 request 基类、不同的初始化步骤、不同的超时时间，生成器会顺着这些属性再拐一层。

所以 Orleans 的 invoker 代码看起来不像一套统一模板，更像一台很多年慢慢长出来的机器。

### 3.5 兼容 invoker 不是“旧实现”，而是“旧命名空间 + 新运行时”

`ProxyGenerator` 生成代理方法。
`InvokableGenerator` 生成 request 对象。
`GenerateCompatibilityInvokers` 额外生成另一套可识别名字。

它们的目的不是重复业务逻辑，而是让不同历史阶段生成出来的名字都能指向同一条运行时调用链。

所以 Orleans 的策略很明确：

旧的名保留，新的是主路径，运行时同时认。

---

## 4. 旧 serializer 兼容：不是 `GenerateSerializer` 之后就没以前那套了

关键文件：

- `src/Orleans.Serialization/ISerializableSerializer/DotNetSerializableCodec.cs`
- `src/Orleans.Serialization/ISerializableSerializer/ExceptionCodec.cs`
- `src/Orleans.Serialization/Invocation/Response.cs`
- `src/Orleans.CodeGenerator/Model/SerializableTypeDescription.cs`
- `src/Orleans.Analyzers/GenerateGenerateSerializerAttributeAnalyzer.cs`
- `src/Orleans.Analyzers/GenerateSerializationAttributesAnalyzer.cs`
- `src/Orleans.Serialization.Abstractions/Annotations.cs`

### 4.1 `[Serializable]` 没被直接砍掉

`GenerateGenerateSerializerAttributeAnalyzer` 还在盯着 `[Serializable]`。

如果一个类型有 `[Serializable]` 但没有 `[GenerateSerializer]`，它会给出提示。

这说明 Orleans 不是“只允许新属性，不允许旧属性”。

它的做法是：

1. 老属性还看得懂
2. 但会提示你往新属性迁移
3. 真正的运行时兼容仍然保留着

### 4.2 `DotNetSerializableCodec` 还在支持 `ISerializable`

`DotNetSerializableCodec` 明确就是给 `.NET ISerializable` 旧模式用的。

它会：

- 读取和写入 `SerializationInfo`
- 处理 `ISerializable.GetObjectData`
- 跑 `OnSerializing` / `OnDeserializing`
- 处理异常的类型名和回退异常

这不是一层薄壳，而是一个完整旧序列化桥。

也就是说，Orleans 不是只靠生成的 `Codec_*`，它还保留了一个专门的老世界入口。

### 4.3 `ExceptionCodec` 是一个典型的折中实现

`ExceptionCodec` 一边是 Orleans 自己的 field codec / copier 路径，另一边又保留了 `SerializationInfo`、`RemoteStackTraceString`、`Exception.Data` 这类 .NET 旧异常语义。

这类类型最说明问题。

它不是普通 DTO，也不可能彻底重写语义，所以 Orleans 只能在兼容和现代化之间折中。

### 4.4 `SerializerTransparent` 把框架包装层藏起来

`Response`、`RequestBase`、`AsyncEnumerableRequest<T>` 这些类型都带着 `SerializerTransparent`。

`SerializableTypeDescription` 和 `InvokableGenerator` 都会在找 base type 时跳过这层。

这件事很关键，因为它把框架包装层从 wire contract 里擦掉了。

表面上看，用户序列化的是 `Response<T>`、`Request<T>` 的子类；
实际上，生成器会把透明基类跳过去，让真正的契约落在更底层的具体类型上。

这就是 Orleans 的另一个历史包袱：

框架自己先背着一层类层次，再努力不让这层层次污染用户看到的协议。

---

## 5. 运行时这边，兼容是怎么被吃进去的

关键文件：

- `src/Orleans.Serialization/Hosting/ServiceCollectionExtensions.cs`
- `src/Orleans.Serialization/Hosting/SerializerBuilderExtensions.cs`
- `src/Orleans.Serialization/Configuration/TypeManifestOptions.cs`
- `src/Orleans.Serialization/Configuration/ITypeManifestProvider.cs`
- `src/Orleans.Serialization/Configuration/DefaultTypeManifestProvider.cs`
- `src/Orleans.Serialization/Serializers/CodecProvider.cs`
- `src/Orleans.Serialization/TypeSystem/RuntimeTypeNameFormatter.cs`
- `src/Orleans.Serialization/TypeSystem/CompoundTypeAliasTree.cs`
- `src/Orleans.Serialization/TypeSystem/TypeConverter.cs`
- `src/Orleans.Serialization/GeneratedCodeHelpers/OrleansGeneratedCodeHelper.cs`

### 5.1 `TypeManifestOptions` 是运行时的兼容账本

运行时不会自己猜哪些类型要兼容。

它靠 `TypeManifestOptions` 这张账本：

- 哪些 serializer 可用
- 哪些 copier 可用
- 哪些 converter 可用
- 哪些 proxy 可用
- 哪些 alias 可用
- 哪些 compound alias 可用

这意味着兼容性不是散在生成代码里就完了，还得在运行时装配成清单。

这也是为什么 Orleans 同时要生成 `ApplicationPartAttribute` 和 `TypeManifestProviderAttribute`。前者负责把相关程序集找回来，后者负责把清单注册进去，少一个都接不住。

### 5.2 `DefaultTypeManifestProvider` 给系统补了基础类型和旧类型入口

`DefaultTypeManifestProvider` 会把一批 well-known type id 预先填进去。

里面包括：

- `DotNetSerializableCodec`
- `ExceptionCodec`
- `ExceptionResponse`
- `CompletedResponse`
- 以及常见的基础类型和数组类型

这说明 Orleans 默认就把老式序列化和异常响应当成系统能力，而不是插件。

### 5.3 `CodecProvider` 仍然会落回旧 codec

`CodecProvider` 不是只会找 `Codec_*`。

它还会找：

- `IGeneralizedCodec`
- `ISpecializableCodec`
- `IBaseCodec`
- `IValueSerializer`
- `IActivator`

所以只要老路径还在 manifest 里，运行时就还是会吃进去。

### 5.4 `TypeConverter` 和 alias 树一起把旧名字接住

`TypeConverter` 负责的不是业务对象，而是类型名。

它把：

- well-known alias
- compound alias
- allowed types
- type filters

这些东西一起揉进了解析链。

`RuntimeTypeNameFormatter` 还会尊重 compound alias，把类型名格式化成运行时能解析的样子。
`CompoundTypeAliasTree` 则把这些别名拼成一棵树，方便生成器和运行时一起维护。

兼容 invoker 和 legacy serializer 之所以还能继续工作，很大一部分原因就是类型名还认得出来。

### 5.5 `OrleansGeneratedCodeHelper` 说明生成代码不是孤岛

生成代码在构造 serializer / copier / activator / invoker 时，常常会通过 `OrleansGeneratedCodeHelper.GetService(...)` 去拿服务。

这意味着：

- 生成器只负责拼壳
- 真正的对象图、包装器、依赖解析还是运行时做

所以 Orleans 的 codegen 从来不是“纯静态代码展开”，它本身就是运行时装配的一部分。

---

## 6. 还有一条更老的历史包袱：legacy grain id

关键文件：

- `src/Orleans.Core.Abstractions/IDs/Legacy/LegacyGrainId.cs`
- `src/Orleans.Core.Abstractions/IDs/GrainTypePrefix.cs`

这一块虽然不完全是 codegen，但它很能说明 Orleans 对历史兼容的态度。

`LegacyGrainId` 和 `GrainTypePrefix` 还保留着：

- `sys.grain.v1.`
- `sys.svc.`
- `sys.client`

这些前缀和转换方法。

这意味着 Orleans 的现代 `GrainId` 体系不是把旧 ID 体系直接删掉，而是继续能从旧形态转换回来。

这也是它一贯的做法：

宁可把旧路留着，也不直接把旧数据砍死。

---

## 7. 为什么说 Orleans 的 codegen 不只是新链路

把上面几层收起来看，答案其实很简单。

它不是只在做“生成新类”这件事，而是在同时做四件事：

1. 给新接口和新 DTO 生成代码。
2. 给旧接口和旧 serializer 留兼容入口。
3. 把兼容产物写回 manifest 和 application part。
4. 让运行时依然能按旧名字、旧属性、旧 ID 认出这些东西。

这就解释了为什么 Orleans 的 codegen 总给人一种“既现代又厚重”的感觉。

它不是一条干净的编译流水线，而是一个把历史协议也一起编进去的装配系统。

---

## 8. 这套兼容层里，我觉得不够干净的地方

### 8.1 编译期开关和运行时行为绑得太紧

`GenerateCompatibilityInvokers` 这种开关直接影响生成物数量和命名空间结构。

这会让构建输出和运行时行为一起变大，后期排查会很烦。

### 8.2 `SerializerTransparent` 让基类语义有点隐式

它能救 compatibility，但也会让阅读者很难第一眼看出谁是契约基类、谁只是包装层。

这类“透明层”用得越多，框架越难直观理解。

### 8.3 兼容 invoker 和正常 invoker 的边界不够清楚

新旧 invoker 其实做的是同一件事，只是名字和归属不同。

对使用者来说，这很好。
对维护者来说，就意味着同一条链会有两份名字、两套 alias、两份 manifest 入口。

### 8.4 `ISerializable`、`ExceptionCodec`、legacy grain id 还在一起活着

这不是错，但说明 Orleans 的 runtime 不是真正“清场后重做”。

它是把一层又一层旧语义叠在新语义上。

这会让系统长期保持兼容，但也会长期背负复杂度。

---

## 9. 如果你要自己复刻，我会怎么拆

如果目标是“兼容历史，但架构比 Orleans 更清楚”，我会这么拆：

### 9.1 单独做一层兼容清单

把：

- 旧 serializer
- 旧 invoker
- 旧 alias
- 旧 ID

都放进一个明确的 compatibility manifest，而不是分散在 generator、runtime 和 analyzer 里。

### 9.2 透明包装层不要跟主契约层混写

`SerializerTransparent` 这种概念可以保留，但最好作为显式 contract metadata，而不是靠类层次隐式传递。

### 9.3 旧名字只做解析，不参与新生成逻辑

新的生成器只管新链路。

老名字单独做 resolver 和 adapter。

这样可以避免兼容逻辑一层层渗进生成器核心。

### 9.4 旧序列化只保留一条桥

像 `DotNetSerializableCodec`、`ExceptionCodec` 这种老桥，保留一个统一入口就够了。

不要让每个子系统都再自己补一套“为了兼容而兼容”的特殊处理。

真要复刻，最好把这些旧桥收进单独的 compatibility layer 里，别让 generator、analyzer 和 runtime 三头都各自长一遍。

---

## 10. 这一篇最该反复看的源码文件

建议按这个顺序读：

1. `src/Orleans.CodeGenerator/OrleansSourceGenerator.cs`
2. `src/Orleans.CodeGenerator/CodeGenerator.cs`
3. `src/Orleans.CodeGenerator/InvokableGenerator.cs`
4. `src/Orleans.CodeGenerator/SerializerGenerator.cs`
5. `src/Orleans.CodeGenerator/MetadataGenerator.cs`
6. `src/Orleans.Serialization/Configuration/TypeManifestProviderAttribute.cs`
7. `src/Orleans.Serialization/Configuration/ITypeManifestProvider.cs`
8. `src/Orleans.Serialization/Configuration/TypeManifestOptions.cs`
9. `src/Orleans.Serialization/Configuration/DefaultTypeManifestProvider.cs`
10. `src/Orleans.Core.Abstractions/Runtime/GrainReference.cs`
11. `src/Orleans.Core.Abstractions/Runtime/AsyncEnumerableRequest.cs`
12. `src/Orleans.Serialization/ISerializableSerializer/DotNetSerializableCodec.cs`
13. `src/Orleans.Serialization/ISerializableSerializer/ExceptionCodec.cs`
14. `src/Orleans.Serialization/TypeSystem/CompoundTypeAliasTree.cs`
15. `src/Orleans.Serialization/TypeSystem/RuntimeTypeNameFormatter.cs`
16. `src/Orleans.Serialization/TypeSystem/TypeConverter.cs`
17. `src/Orleans.Serialization/GeneratedCodeHelpers/OrleansGeneratedCodeHelper.cs`
18. `src/Orleans.Core.Abstractions/IDs/Legacy/LegacyGrainId.cs`
19. `src/Orleans.Core.Abstractions/IDs/GrainTypePrefix.cs`
20. `src/Orleans.Analyzers/GenerateGenerateSerializerAttributeAnalyzer.cs`

如果把这 20 个文件顺一遍，你会很清楚地看到：Orleans 的 codegen 不是一条干净的新线，而是把旧线、兼容线、运行时装配线一起揉进去了。

---

## 11. 这一篇的落点

把这一篇压成一句话，就是：

Orleans 的代码生成不是“从旧世界切到新世界”，而是“先让新世界站稳，再把旧世界一层层收进适配层里”。

它能长期兼容，代价就是：

- generator 变厚
- manifest 变厚
- runtime 变厚
- attribute 语义变厚

这不是坏事，但它确实不干净。

如果你后面要复刻一版更清爽的系统，我会建议把兼容层单独拎出来，别让它继续长进主干里。
