# 编译期代码生成链路：代理、Invokable、Serializer 是怎么拼起来的（第三篇）

这一篇专门回答一个问题：

为什么 Orleans 能把你写的普通接口方法，变成一条“强类型代理调用 -> 可序列化请求对象 -> 远端执行 -> 结果反序列化返回”的链，而且大部分细节都不用你手写。

如果只看运行时，很容易以为 Orleans 的核心是 `GrainReferenceRuntime`、`PlacementService`、`ActivationData`。这当然没错，但只说一半。

另一半在编译期。

Orleans 现在这套模型，本质上是四段东西拼起来的：

1. 编译期扫描用户代码和相关程序集
2. 生成代理、`Invokable_*`、`Codec_*`、`Copier_*`、`Activator_*`
3. 再生成一份 assembly 级 metadata provider
4. 运行时启动时把这份 metadata 吃进去，拿来驱动代理创建、类型解析、序列化和深拷贝

所以你看到的不是“三类彼此独立的生成代码”，而是一整条闭环。

---

## 1. 先把整条链压成一张图

```text
用户代码
  - grain interface : IAddressable / IGrain...
  - DTO / state / request / response : [GenerateSerializer]
  - 可选属性 : [Id] [Alias] [GeneratedActivatorConstructor] ...
    ↓
Microsoft.Orleans.Sdk
  - 把 Orleans.CodeGenerator 当 source generator / analyzer 带进编译
    ↓
OrleansSerializationSourceGenerator
  - 创建 CodeGenerator
  - 扫描当前编译 + 指定关联程序集
    ↓
CodeGenerator 建模
  - SerializableTypes
  - InvokableInterfaces
  - GeneratedInvokables
  - GeneratedProxies
  - ActivatableTypes
  - TypeAliases / WellKnownTypeIds / CompoundTypeAliases
    ↓
生成代码
  - Proxy_IFooGrain
  - Invokable_IFooGrain_GrainReference_XXXXXXXX
  - Codec_BarDto / Copier_BarDto / Activator_BarDto
  - Codec_Invokable_xxx / Copier_Invokable_xxx
  - Metadata_MyAssembly
  - [assembly: ApplicationPart(...)]
  - [assembly: TypeManifestProvider(...)]
    ↓
运行时启动
  - AddSerializer -> 扫 ApplicationPart 程序集
  - 读取 TypeManifestProviderAttribute
  - 把生成的类型注册进 TypeManifestOptions
    ↓
运行时消费
  - GrainReferenceActivator.RpcProvider 用 InterfaceProxies 找代理类型
  - CodecProvider 用 Serializers/Copiers/Activators 找生成代码
  - TypeConverter 用别名和 compound alias 解析类型名
```

只要这张图顺了，Orleans 的“透明远程调用”就没那么玄了。

---

## 2. Orleans 到底生成了哪些东西

先别急着看实现，先记住产物。

以你写的这段代码为例：

```csharp
public interface IOrderGrain : IGrainWithGuidKey
{
    Task<OrderDto> GetOrder();
    ValueTask Cancel(string reason);
}

[GenerateSerializer]
public sealed class OrderDto
{
    [Id(0)] public Guid Id { get; set; }
    [Id(1)] public string Status { get; set; }
}
```

Orleans 最终通常会补出这几类东西：

- `Proxy_IOrderGrain`
- `Invokable_IOrderGrain_GrainReference_xxxxxxxx`
- `Invokable_IOrderGrain_GrainReference_yyyyyyyy`
- `Codec_OrderDto`
- `Copier_OrderDto`
- `Activator_OrderDto`，如果这个类型满足 activator 生成条件
- `Codec_Invokable_IOrderGrain_...`
- `Copier_Invokable_IOrderGrain_...`
- `Metadata_YourAssembly`

重点不是“类名多”，而是它们之间有依赖关系：

- 代理方法会 new 或获取 `Invokable_*`
- `Invokable_*` 自己也是可序列化类型，所以又会生成 `Codec_*` 和 `Copier_*`
- 这些 serializer/copier/activator 又会被写进 `Metadata_*`
- 运行时只认 `Metadata_*` 暴露出来的清单

所以 Orleans 这里不是“看到接口就生成代理，看到 DTO 就生成 serializer”这么简单，而是把所有生成物最后都汇总成一份 manifest。

---

## 3. 编译入口：`Microsoft.Orleans.Sdk` 到底做了什么

关键文件：

- `src/Orleans.Sdk/Orleans.Sdk.csproj`
- `src/Orleans.CodeGenerator/Orleans.CodeGenerator.csproj`
- `src/Orleans.CodeGenerator/build/Microsoft.Orleans.CodeGenerator.props`

先说结论：

`Microsoft.Orleans.Sdk` 本身几乎不写生成逻辑，它更像一个打包入口。

它做的事主要有两件：

1. 引用 `Orleans.CodeGenerator`
2. 让 `Orleans.CodeGenerator` 以 Roslyn analyzer/source generator 的身份进入用户编译

`Orleans.CodeGenerator.csproj` 里有几个很关键的信号：

- `IsRoslynComponent=true`
- `IncludeBuildOutput=false`
- 把生成出来的 dll 打到 `analyzers/dotnet/cs`

这就说明它不是普通运行时程序集，而是编译器插件。

另外，`Microsoft.Orleans.CodeGenerator.props` 会把几个 MSBuild 属性暴露给编译器：

- `Orleans_DesignTimeBuild`
- `Orleans_AttachDebugger`
- `Orleans_GenerateFieldIds`
- `Orleans_ConstructorAttributes`
- `OrleansGenerateCompatibilityInvokers`

后面 `OrleansSerializationSourceGenerator` 会直接从这些 build property 里读配置。

这一段很像一个判断题：

- `Orleans.Sdk` 不是核心逻辑所在
- 但没有它，用户项目就拿不到这套 source generator

---

## 4. 真正入口：`OrleansSerializationSourceGenerator`

关键文件：

- `src/Orleans.CodeGenerator/OrleansSourceGenerator.cs`

这个类名有点旧，它现在早就不只生成 serialization 代码了，代理和 invokable 也都从这里出。

它做的事很直接：

1. 跳过 `devenv` / `servicehub` 这类设计时进程
2. 设计时构建时默认也跳过
3. 从 build property 里读取生成选项
4. new 一个 `CodeGenerator`
5. 调 `GenerateCode(...)`
6. 把结果作为一个单独的 source 文件塞回编译器

最后的文件名是：

```text
<AssemblyName>.orleans.g.cs
```

也就是说，Orleans 默认不是给你拆成几十个 generated file，而是把这一轮生成结果收进一个大的 `.orleans.g.cs`。

这点有利有弊：

- 好处是输出集中，产物稳定
- 坏处是单文件会越来越大，定位具体生成块不算特别舒服

另外，还有一个挺值得记的点：

它现在还是经典的 `ISourceGenerator`，不是 `IIncrementalGenerator`。

这件事不影响语义，但如果你以后自己复刻，我会认真考虑直接做成 incremental generator。Orleans 这块明显带点历史包袱。

---

## 5. `CodeGenerator` 的真正角色：先建一份内存里的“装配清单”

关键文件：

- `src/Orleans.CodeGenerator/CodeGenerator.cs`
- `src/Orleans.CodeGenerator/Model/MetadataModel.cs`

`CodeGenerator` 干的事，不是“边扫边吐代码”。

它更像先做一轮建模，再统一生成。

中间这份模型就是 `MetadataModel`，里面最重要的几类集合是：

- `SerializableTypes`
- `InvokableInterfaces`
- `GeneratedInvokables`
- `GeneratedProxies`
- `ActivatableTypes`
- `DetectedSerializers`
- `DetectedCopiers`
- `DetectedActivators`
- `DetectedConverters`
- `TypeAliases`
- `WellKnownTypeIds`
- `CompoundTypeAliases`
- `ApplicationParts`

你可以把它理解成：

“这一轮编译里，Orleans 认出来了哪些需要代码生成的类型、接口、别名、代理、请求对象和注册项。”

后面的 `ProxyGenerator`、`InvokableGenerator`、`SerializerGenerator`、`MetadataGenerator`，其实都是在消费这份模型。

这也是 Orleans 这块写得比较像“编译器前后端”的地方。

---

## 6. 它到底扫描哪些东西

这一段是整条链最关键的入口条件。

### 6.1 当前编译总会扫

`CodeGenerator.GenerateCode()` 默认一定会扫当前 compilation 的 assembly。

这没什么悬念。

### 6.2 不是所有引用程序集都会被重新扫一遍

这个点很容易想当然。

真正会被 `ComputeAssembliesToExamine(...)` 继续递归展开的，是带了：

- `[assembly: GenerateCodeForDeclaringAssembly(typeof(SomeType))]`

的程序集。

也就是说，Orleans 不会无脑把所有引用程序集重新分析一遍。它默认主要处理当前编译单元，跨程序集扩展靠显式标记。

### 6.3 已经生成过的引用程序集会被当成 application part 带进来

`GenerateCode()` 还会扫引用里的 assembly attribute：

- `[ApplicationPart(...)]`

如果某个引用程序集已经带了 Orleans 生成过的 application part 信息，它会被纳入 `MetadataModel.ApplicationParts`。

这个动作本身不代表“重生成”，而是为后面的 runtime 装配做准备。

这一层经常被看混：

- `assembliesToExamine` 是本轮要分析和生成的范围
- `ApplicationParts` 是运行时最终要串起来的程序集集合

两者有关，但不是一回事。

---

## 7. 哪些类型会进 `SerializableTypes`

最典型的入口当然是：

- `[GenerateSerializer]`

关键文件：

- `src/Orleans.Serialization.Abstractions/Annotations.cs`

`CodeGenerator` 会把带 `[GenerateSerializer]` 的 class/struct/enum 收进来，然后为它们生成 serializer/copy/activator。

这里还有几条很重要的规则。

### 7.1 成员默认靠 `[Id]`

默认模式下，`[GenerateSerializer]` 类型里的可序列化成员要有唯一的 `[Id(n)]`。

如果没有显式 id，默认不会替你兜底。

### 7.2 可以开启自动 field id

有两种方式：

- 全局 MSBuild 属性 `OrleansGenerateFieldIds`
- 类型级别 `GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties)`

当前实现里只支持一个自动策略：

- `PublicProperties`

也就是给符合条件的公共属性自动分配 field id。

### 7.3 主构造参数可以自动并入

`GenerateSerializerAttribute` 还有一个开关：

- `IncludePrimaryConstructorParameters`

对 `record`，默认更偏向自动把主构造参数视作 serializable member。

### 7.4 F# record / union 也有专项分支

`CodeGenerator` 里专门走了 `FSharpUtilities`。

也就是说，Orleans 不是只考虑 C# POCO，它在生成器层已经显式照顾了 F# record 和 union。

---

## 8. 哪些接口会生成代理和 invokable

这里的关键不是你有没有直接写 `[GenerateMethodSerializers]`，而是这个属性会不会被继承到你的接口上。

关键文件：

- `src/Orleans.Core.Abstractions/Runtime/IAddressable.cs`
- `src/Orleans.Core.Abstractions/Runtime/IGrainExtension.cs`
- `src/Orleans.Serialization.Abstractions/Annotations.cs`
- `src/Orleans.CodeGenerator/CodeGenerator.cs`

`IAddressable` 上有：

```csharp
[GenerateMethodSerializers(typeof(GrainReference))]
```

`IGrainExtension` 上有：

```csharp
[GenerateMethodSerializers(typeof(GrainReference), isExtension: true)]
```

所以只要你的 Grain 接口最终继承到 `IAddressable`，生成器就会把它识别成“需要 remoting 支持的接口”。

这也是 Orleans 这里很聪明的一点：

用户不用在每个 Grain 接口上重复打“帮我生成代理”的标记。框架通过基接口就把规则兜住了。

`VisitInterface(...)` 做的核心动作就是：

1. 找到接口继承链上的 `GenerateMethodSerializersAttribute`
2. 拿到 proxy base 类型，比如 `GrainReference`
3. 为这个接口构造 `ProxyInterfaceDescription`
4. 进一步生成 proxy 和每个方法对应的 invokable

---

## 9. 代理是怎么生成出来的

关键文件：

- `src/Orleans.CodeGenerator/ProxyGenerator.cs`
- `src/Orleans.CodeGenerator/Model/ProxyInterfaceDescription.cs`

生成出来的代理类大致长这样：

```text
internal sealed class Proxy_IFooGrain : GrainReference, IFooGrain
```

这里有三个关键点。

### 9.1 先校验 proxy base 合不合格

`ProxyInterfaceDescription` 会先检查 proxy base，也就是通常的 `GrainReference`，有没有 Orleans 需要的那组方法。

至少包括：

- `ValueTask<T> InvokeAsync<T>(IInvokable)`
- `ValueTask InvokeAsync(IInvokable)`

也就是说，生成器不是“看你给了个 base class 就瞎生”，而是会验证这个 base class 能不能承接 Orleans 的代理调用模型。

### 9.2 每个接口方法都会生成一个显式实现

代理不会试图复原你的业务代码，它做的事永远是：

1. 创建 `Invokable_*` 请求对象
2. 把方法参数写进请求对象字段
3. 必要时对参数做 deep copy
4. 调 base 上的 `InvokeAsync` / `Invoke`

为什么要 deep copy 参数？

因为请求发出去以后，调用方手里的对象还可能继续被改。Orleans 不希望“消息已经在路上了，原始对象又被改了一次”这种情况把运行时搞脏。

所以 `ProxyGenerator` 会去找参数所需的 copier，没有静态 copier 的话就注入对应服务。

### 9.3 返回值风格由代理层统一适配

这层会根据方法返回类型决定怎么收口：

- `Task<T>` / `Task`
- `ValueTask<T>` / `ValueTask`
- `void`
- `IAsyncEnumerable<T>`

普通 async 返回值最后都会变成对 base `InvokeAsync(...)` 的调用。

`IAsyncEnumerable<T>` 是个特殊分支，它会走 `ReturnValueProxy` 机制。`AsyncEnumerableRequest<T>` 上有：

- `[ReturnValueProxy(nameof(InitializeRequest))]`

所以代理不会直接 `await` 它，而是把请求对象本身初始化成一个“返回值代理”。

这点如果只看运行时非常容易看丢。

---

## 10. `GrainReference` 为什么是这条链的“协议基类”

关键文件：

- `src/Orleans.Core.Abstractions/Runtime/GrainReference.cs`

`GrainReference` 上挂了几组非常关键的属性：

```csharp
[DefaultInvokableBaseType(typeof(ValueTask<>), typeof(Request<>))]
[DefaultInvokableBaseType(typeof(ValueTask), typeof(Request))]
[DefaultInvokableBaseType(typeof(Task<>), typeof(TaskRequest<>))]
[DefaultInvokableBaseType(typeof(Task), typeof(TaskRequest))]
[DefaultInvokableBaseType(typeof(void), typeof(VoidRequest))]
[DefaultInvokableBaseType(typeof(IAsyncEnumerable<>), typeof(AsyncEnumerableRequest<>))]
```

这相当于给生成器塞了一张默认映射表：

- 这个接口方法返回什么
- 对应应该继承哪种 request base

所以 `InvokableGenerator` 并不是自己瞎猜“这个方法该生成什么样的请求类”，而是先看 proxy base 给出的默认规则。

这也是 Orleans 这里很整齐的一点：

代理和 invokable 不是各生各的，它们中间通过 proxy base 上的 attribute 接起来了。

---

## 11. `Invokable_*` 是怎么来的

关键文件：

- `src/Orleans.CodeGenerator/InvokableGenerator.cs`
- `src/Orleans.CodeGenerator/Model/InvokableMethodDescription.cs`

每个远程方法，最后都会落成一个 `Invokable_*` 类。

类名大概是：

```text
Invokable_<ContainingInterface>_<ProxyBaseKey>_<MethodId>
```

比如 Orleans 自己 API 快照里能看到这样的名字：

- `Invokable_IGrainManagementExtension_GrainReference_Ext_1B9614D1`

这个名字里已经编码了几层信息：

- 属于哪个接口
- 代理基类是谁
- 是不是 extension
- 方法 id 是什么

### 11.1 方法 id 不是随便来的

`InvokableMethodDescription` 里有三层优先级：

1. 方法上的 `[Id(...)]`
2. 方法上的 `[Alias(...)]`
3. 如果都没有，就用签名 hash

签名 hash 是对：

```text
ContainingType.MethodName<GenericArgs>(ParameterTypes)
```

做 `XxHash32` 得到的。

这一步非常关键，因为方法 id 最后会进 wire-level 的类型别名体系。

### 11.2 `Invokable_*` 里真正放了什么

生成出来的 invokable 一般会有这些字段：

- `arg0`、`arg1`、`arg2`...
- `_target`
- `MethodBackingField`
- 如果支持取消，还会有 `_cts`

它还会生成一组标准方法：

- `GetArgumentCount`
- `GetArgument`
- `SetArgument`
- `GetMethodName`
- `GetInterfaceName`
- `GetActivityName`
- `GetInterfaceType`
- `GetMethod`
- `SetTarget`
- `GetTarget`
- `InvokeInner`
- `Dispose`
- `GetCancellationToken`
- `TryCancel`

你可以把 `Invokable_*` 理解成一个“可序列化、可反射描述、可执行”的方法调用对象。

它不是单纯 DTO，也不是单纯 command。它同时承担了这三件事：

- 带参数
- 带方法元数据
- 知道如何对目标对象真正发起调用

### 11.3 真正业务调用落在 `InvokeInner`

生成器最终会给它补一个：

```text
protected override <ReturnType> InvokeInner()
    => _target.Method(arg0, arg1, ...)
```

这就是为什么运行时到了 `InsideRuntimeClient.Invoke -> request.Invoke()` 以后，就能真正落到 Grain 实现上。

---

## 12. 方法上的 attribute 是怎么影响 invokable 的

这一步很容易低估，但它其实是 Orleans 很多调用语义的入口。

关键文件：

- `src/Orleans.CodeGenerator/Model/InvokableMethodDescription.cs`
- `src/Orleans.Serialization.Abstractions/Annotations.cs`
- `src/Orleans.Core.Abstractions/Concurrency/GrainAttributeConcurrency.cs`

`InvokableMethodDescription` 在分析方法 attribute 的时候，会额外处理几类东西。

### 12.1 `InvokableBaseTypeAttribute`

它可以覆盖默认的 request base 选择规则。

也就是说，默认是：

- `Task<T> -> TaskRequest<T>`

但某些 attribute 可以改成另一套 invokable base。

### 12.2 `InvokableCustomInitializerAttribute`

这个很关键。

有些方法 attribute 本身不直接改代理方法体，而是告诉生成器：

- 生成出来的 invokable 构造完以后，帮我调一下某个初始化方法

例如并发相关 attribute 会把：

- `InvokeMethodOptions.ReadOnly`
- `InvokeMethodOptions.AlwaysInterleave`
- `InvokeMethodOptions.OneWay`

通过 `AddInvokeMethodOptions(...)` 写进请求对象。

这意味着 Orleans 很多“调用语义”不是靠运行时硬编码识别某个 attribute 名，而是编译期就把语义烘进了请求对象。

这比运行时到处读反射干净得多。

### 12.3 `ResponseTimeoutAttribute`

如果方法上带了超时配置，生成器会把 timeout ticks 也烘进 invokable 的 `GetDefaultResponseTimeout()`。

这样发送端超时控制就能按方法粒度生效。

---

## 13. `Invokable_*` 自己为什么还要继续生成 serializer

这是 Orleans 这条链最容易漏掉、但又最重要的一个连接点。

在 `CodeGenerator.GetProxyMethodDescription(...)` 里，一旦生成了一个新的 invokable，它会立刻做两件事：

1. `AddMember(...)` 把 `Invokable_*` 类写进生成结果
2. 把这个 invokable 加进 `MetadataModel.SerializableTypes`

第二步特别关键。

因为这意味着：

- `Invokable_*` 不是只负责“本地包装方法调用”
- 它自己还是一个 Orleans 序列化类型

所以它后面还会继续生成：

- `Codec_Invokable_*`
- `Copier_Invokable_*`
- 某些情况下还有 `Activator_Invokable_*`

你在 API 快照里其实能直接看到这套组合：

- `Invokable_IGrainManagementExtension_...`
- `Codec_Invokable_IGrainManagementExtension_...`
- `Copier_Invokable_IGrainManagementExtension_...`

这就是 Orleans 这里真正闭环的地方：

代理创建出的请求对象，不是交给某个通用反射 serializer，而是立刻又落回 Orleans 自己生成的 serializer 体系。

---

## 14. Serializer 是怎么生成出来的

关键文件：

- `src/Orleans.CodeGenerator/SerializerGenerator.cs`

生成出来的 serializer 一般叫：

- `Codec_<TypeName>`

它不是简单实现一个接口就完了，而是会根据目标类型形态选不同基类。

常见几种情况：

- 普通类型：实现 `IFieldCodec<T>`
- struct：通常还会带 `IValueSerializer<T>`
- 非 sealed 引用类型：还可能带 `IBaseCodec<T>`
- abstract 类型：走 `AbstractTypeSerializer<T>`

### 14.1 Serializer 内部会注入依赖

它会根据成员类型决定要不要持有：

- 某个成员类型对应的 codec
- base type serializer
- activator
- hook 对象

这些依赖不是手搓 service locator，而是通过生成构造函数 + `OrleansGeneratedCodeHelper.GetService(...)` 去拿。

这也是 Orleans 代码生成和 DI 结合得比较紧的一块。

### 14.2 写字段时按 FieldId 顺序输出

`Serialize(...)` 会按 field id 排序写成员，因为 Orleans 的 wire format 里字段 id 是按 delta 编码的。

如果类型开了“省略默认值”，它还会在写之前判断：

- 值类型是不是默认值
- 引用类型是不是 null

满足就跳过。

### 14.3 读字段时走一个 id 驱动的循环

`Deserialize(...)` 会：

1. 读 field header
2. 累加 `FieldIdDelta`
3. 按 id 命中具体成员
4. 用对应 codec 去读值
5. 不认识的字段就 `ConsumeUnknownField`

这块不是 JSON 风格的“按名字找字段”，而是一套更低层、更固定格式的字段协议。

### 14.4 继承、引用跟踪、回调、主构造参数都在这里处理

SerializerGenerator 还会处理这些分支：

- base type 序列化
- `SuppressReferenceTracking`
- `SerializationCallbacks`
- 主构造参数先读后构造
- `UseActivator`

也就是说，Orleans 这套 generated serializer 其实已经很接近“类型专用编译器产物”了，不是普通样板代码。

---

## 15. Copier 为什么和 Serializer 同等重要

关键文件：

- `src/Orleans.CodeGenerator/CopierGenerator.cs`

很多人第一次读 Orleans 会把 copier 当配角，这其实不对。

对 Orleans 来说，copier 至少有三件实打实的用途：

1. 代理发请求前复制参数
2. 运行时在某些边界上复制响应
3. Orleans 自己做对象图隔离，避免共享可变状态

生成出来的 copier 一般叫：

- `Copier_<TypeName>`

它会根据类型形态决定：

- 直接用 `ShallowCopier<T>`
- 生成 `IDeepCopier<T>`
- 对引用类型额外实现 `IBaseCopier<T>`
- 对异常类型走专门分支

这里要特别记一句：

Orleans 的 serializer 和 copier 是一对儿，不是 serializer 主角、copier 附带。

如果你以后复刻时只把 serializer 这条线照着做，最后大概率会在“调用参数被后改”“状态对象共享引用”“深拷贝边界不清”这些地方踩坑。

---

## 16. Activator 是怎么插进来的

关键文件：

- `src/Orleans.CodeGenerator/ActivatorGenerator.cs`
- `src/Orleans.Serialization.Abstractions/Annotations.cs`

生成出来的 activator 一般叫：

- `Activator_<TypeName>`

它实现的是：

- `IActivator<T>`

这里得先把一个误区掰开：

这个 activator 不是给 Grain activation 用的，它主要服务的是 Orleans 序列化体系里“我需要创建一个该类型实例来反序列化或复制”这类场景。

它通常在两种情况下很有用：

1. 类型有可直接调用的默认构造
2. 类型标了 `[GeneratedActivatorConstructor]`，需要通过构造函数注入依赖

如果类型标了 `[UseActivator]`，生成 serializer 时还会显式依赖 `IActivator<T>`。

这说明 Orleans 这里不是死盯“new 一个对象”，而是允许把对象创建策略本身也纳入生成代码体系。

---

## 17. Metadata 是怎么把这些散件收口的

关键文件：

- `src/Orleans.CodeGenerator/MetadataGenerator.cs`
- `src/Orleans.Serialization/Configuration/ITypeManifestProvider.cs`
- `src/Orleans.Serialization/Configuration/TypeManifestOptions.cs`

这是整条链从“很多生成类”收敛回“一份可消费 manifest”的地方。

`MetadataGenerator` 会生成一个：

- `Metadata_<AssemblyName>`

它继承：

- `TypeManifestProviderBase`

然后在 `ConfigureInner(TypeManifestOptions config)` 里把本 assembly 生成出来的内容一股脑加进去：

- `config.Serializers.Add(...)`
- `config.Copiers.Add(...)`
- `config.Converters.Add(...)`
- `config.InterfaceProxies.Add(...)`
- `config.Interfaces.Add(...)`
- `config.InterfaceImplementations.Add(...)`
- `config.Activators.Add(...)`
- `config.WellKnownTypeIds.Add(...)`
- `config.WellKnownTypeAliases.Add(...)`
- `config.CompoundTypeAliases.Add(...)`

注意这里有个特别关键的设计：

运行时后面基本不直接“扫所有生成类型”，它主要吃这份 manifest。

所以 Orleans 不是“生成代码 + 运行时反射大海捞针”，而是“生成代码 + 生成注册表 + 运行时按注册表消费”。

这个思路非常对。

---

## 18. Assembly 级 attribute 是怎么把 metadata 暴露出去的

关键文件：

- `src/Orleans.CodeGenerator/CodeGenerator.cs`
- `src/Orleans.CodeGenerator/ApplicationPartAttributeGenerator.cs`
- `src/Orleans.Serialization.Abstractions/Annotations.cs`
- `src/Orleans.Serialization/Configuration/TypeManifestProviderAttribute.cs`

生成代码最后还会补两类 assembly attribute。

### 18.1 `TypeManifestProviderAttribute`

形态大概是：

```csharp
[assembly: TypeManifestProvider(typeof(OrleansCodeGen.<...>.Metadata_MyAssembly))]
```

这告诉运行时：

“这个程序集有一份可以实例化的 type manifest provider。”

### 18.2 `ApplicationPartAttribute`

生成器还会把自己和已知 application part 之间的关系写成：

```csharp
[assembly: ApplicationPart("Some.Assembly")]
```

这件事主要是给 runtime 做程序集发现。

所以你可以把这两个 attribute 分开理解：

- `TypeManifestProviderAttribute` 解决“去哪个类里拿 manifest”
- `ApplicationPartAttribute` 解决“运行时要把哪些 assembly 串起来”

---

## 19. 运行时是怎么把这份 metadata 吃进去的

关键文件：

- `src/Orleans.Serialization/ServiceCollectionExtensions.cs`
- `src/Orleans.Serialization/Hosting/ReferencedAssemblyProvider.cs`
- `src/Orleans.Serialization/Hosting/SerializerBuilderExtensions.cs`

`AddSerializer(...)` 启动时会做几件事：

1. 先找 relevant assemblies
2. relevant assembly 的判定里会看 `ApplicationPartAttribute`
3. 对每个 assembly 调 `builder.AddAssembly(asm)`
4. `AddAssembly` 再去读这个 assembly 上的 `TypeManifestProviderAttribute`
5. 把 provider 类型注册成 `IConfigureOptions<TypeManifestOptions>`

后面 `TypeManifestOptions` 聚齐以后，运行时的几个核心组件就各自消费不同部分：

### 19.1 `GrainReferenceActivator.RpcProvider`

它用的是：

- `TypeManifestOptions.InterfaceProxies`

这就是它为什么能从 Grain 接口类型映射到生成的 `Proxy_*` 类型。

### 19.2 `CodecProvider`

它用的是：

- `Serializers`
- `FieldCodecs`
- `Copiers`
- `Converters`
- `Activators`

所以你生成出来的 `Codec_*`、`Copier_*`、`Activator_*`，最终都会在这里被拿来实例化和分派。

### 19.3 `TypeConverter`

它会吃：

- `WellKnownTypeIds`
- `WellKnownTypeAliases`
- `CompoundTypeAliases`
- `InterfaceProxies`

特别是 `CompoundTypeAliases`，就是用来把 invokable 这类复杂生成类型的 wire-level type name 稳定地解析回真正类型。

这也是 Orleans 这条链里很容易被忽略的一段：别名系统不是附带品，它是跨节点类型识别的一部分。

---

## 20. 为什么 `CompoundTypeAlias` 这套东西很重要

关键文件：

- `src/Orleans.CodeGenerator/InvokableGenerator.cs`
- `src/Orleans.CodeGenerator/Model/MetadataModel.cs`
- `src/Orleans.Serialization.Abstractions/Annotations.cs`
- `src/Orleans.Serialization/TypeSystem/TypeConverter.cs`

普通 DTO 可能只靠：

- well-known type id
- alias

就够了。

但 invokable 这种类型不太一样。它天然带这些维度：

- proxy base 是谁
- 目标接口是谁
- 是不是 extension
- 方法 id 是什么

所以 Orleans 会给 invokable 额外挂 `CompoundTypeAlias`。

你在 API 快照里能看到类似：

```text
("inv", typeof(GrainReference), "Ext", typeof(IGrainManagementExtension), typeof(IGrainManagementExtension), "1B9614D1")
```

这不是为了好看，而是为了让运行时可以用一棵 alias tree 稳定地把“远端传来的 invokable 类型描述”还原回具体 CLR 类型。

如果没有这层，跨程序集、跨版本、带泛型的 invokable 解析会很快变脆。

---

## 21. 用一个完整例子把几类生成物串起来

假设你有这样一个 Grain 接口：

```csharp
public interface IOrderGrain : IGrainWithGuidKey
{
    [ReadOnly]
    Task<OrderDto> GetOrder(Guid id, CancellationToken ct = default);
}
```

`OrderDto` 标了 `[GenerateSerializer]`。

大致会发生这些事。

### 21.1 接口识别

因为 `IOrderGrain -> IGrain -> IAddressable`，所以它继承到了：

- `[GenerateMethodSerializers(typeof(GrainReference))]`

生成器知道它要生成 remoting 支持。

### 21.2 代理生成

会生成：

- `Proxy_IOrderGrain`

这个代理实现 `IOrderGrain`，继承 `GrainReference`。`GetOrder(...)` 的方法体不会写真逻辑，只会：

1. 造一个 `Invokable_IOrderGrain_...`
2. 把 `id` 和 `ct` 写进去
3. 调 base `InvokeAsync<OrderDto>(request)`

### 21.3 invokable 生成

会生成：

- `Invokable_IOrderGrain_GrainReference_XXXXXXXX`

它大概继承 `TaskRequest<OrderDto>`，并且：

- 有 `arg0`、`arg1`
- 有 `_target`
- 有 `MethodBackingField`
- 有取消 token 支持
- 构造时会把 `[ReadOnly]` 之类的方法语义写进 `Options`

### 21.4 invokable 自己也继续生成 serializer/copier

因为这个 invokable 会进网络，所以又会生成：

- `Codec_Invokable_IOrderGrain_...`
- `Copier_Invokable_IOrderGrain_...`

### 21.5 DTO 生成 serializer/copier/activator

`OrderDto` 会生成：

- `Codec_OrderDto`
- `Copier_OrderDto`
- 如果满足条件，再加 `Activator_OrderDto`

### 21.6 metadata 汇总

最后这些都会被 `Metadata_YourAssembly` 收进去。

运行时启动时：

- `RpcProvider` 找到 `Proxy_IOrderGrain`
- `CodecProvider` 找到 `Codec_OrderDto`、`Codec_Invokable_...`
- `TypeConverter` 认得这些 alias

整条链到这里才真正闭合。

---

## 22. 站在复刻角度，我觉得 Orleans 这套设计最值钱的地方

这一篇读完以后，我觉得有三点特别值得原样吸收。

### 22.1 代理、请求对象、序列化器必须一起生成

这三件事如果拆开，各自独立设计，最后很容易变成三套协议。

Orleans 这里做得对的地方是：

- 代理只负责构造 request
- request 自己就是可序列化对象
- serializer/copy/activator 又统一纳入 metadata

最后变成一套闭环。

### 22.2 运行时最好吃 manifest，不要到处反射找类型

`Metadata_* + TypeManifestOptions` 这套设计非常值。

它让运行时的核心问题从：

“程序集里到底有哪些生成类型，我现在怎么用反射把它们找出来”

变成：

“manifest 已经告诉我有哪些类型，我按分类消费就行”

这会让系统更稳，也更容易调试。

### 22.3 方法语义最好在编译期烘进请求对象

像 `ReadOnly`、`OneWay`、`AlwaysInterleave` 这些语义，如果等到运行时再到处读 attribute，会越来越乱。

Orleans 现在的处理方式是：

- 方法 attribute
  -> 编译期转成 initializer
  -> initializer 写进 request options

这条线很干净。

---

## 23. 但这套实现也有几个不太舒服的地方

如果目标是“完整复刻，但架构更干净”，我会先盯这几个点。

### 23.1 生成器入口还是经典 `ISourceGenerator`

这不是功能问题，是工程问题。

Orleans 这套扫描逻辑已经不小了，继续放在老式 source generator 里，增量能力和调试体验都不算理想。

### 23.2 `OrleansSerializationSourceGenerator` 这个名字已经不对了

它现在生成的远不只是 serialization。

名字和职责脱节，读代码时很容易先被误导。

### 23.3 codegen 规则分散在 attribute、proxy base、helper、metadata 几层里

这不是说它乱，而是入口有点多。

例如一个方法最终怎么生成，要同时看：

- 接口是否继承到 `GenerateMethodSerializers`
- proxy base 上的 `DefaultInvokableBaseType`
- 方法上的 `Id` / `Alias`
- 方法 attribute 带来的 `InvokableCustomInitializer`
- serializer 侧的 type alias / compound alias

这对 Orleans 这种成熟框架是能接受的，但对你自己复刻第一版来说，最好先收缩规则。

---

## 24. 如果你自己复刻，我会怎么裁这条链

我会建议第一版只保留下面这些机制：

1. 一个真正的 incremental source generator
2. 只支持 C#，先不碰 F#
3. grain interface 统一继承一个基接口，沿用继承式 `GenerateMethodSerializers`
4. 只支持 `Task<T>` / `ValueTask<T>` / `Task` / `ValueTask`
5. 每个远程方法生成一个 `Invokable_*`
6. 每个 serializable 类型生成 `Codec_*` 和 `Copier_*`
7. `Metadata_*` 一定保留

第一版我反而不会急着做这些：

- 兼容 invoker
- 太复杂的 attribute 组合
- 特殊返回值代理
- 太多 alias 变体

因为 Orleans 现在这条线最值钱的，不是“特性全”，而是“这几类生成物之间有完整闭环”。

把闭环先复刻出来，比一开始追特性数更重要。

---

## 25. 建议你下一轮重点读的文件

如果你接下来要顺着这篇继续抠源码，我建议按这个顺序读：

1. `src/Orleans.CodeGenerator/OrleansSourceGenerator.cs`
2. `src/Orleans.CodeGenerator/CodeGenerator.cs`
3. `src/Orleans.CodeGenerator/Model/MetadataModel.cs`
4. `src/Orleans.Serialization.Abstractions/Annotations.cs`
5. `src/Orleans.Core.Abstractions/Runtime/IAddressable.cs`
6. `src/Orleans.Core.Abstractions/Runtime/GrainReference.cs`
7. `src/Orleans.CodeGenerator/Model/ProxyInterfaceDescription.cs`
8. `src/Orleans.CodeGenerator/ProxyGenerator.cs`
9. `src/Orleans.CodeGenerator/Model/InvokableMethodDescription.cs`
10. `src/Orleans.CodeGenerator/InvokableGenerator.cs`
11. `src/Orleans.CodeGenerator/SerializerGenerator.cs`
12. `src/Orleans.CodeGenerator/CopierGenerator.cs`
13. `src/Orleans.CodeGenerator/ActivatorGenerator.cs`
14. `src/Orleans.CodeGenerator/MetadataGenerator.cs`
15. `src/Orleans.Serialization/Hosting/SerializerBuilderExtensions.cs`
16. `src/Orleans.Serialization/Hosting/ReferencedAssemblyProvider.cs`
17. `src/Orleans.Serialization/Serializers/CodecProvider.cs`
18. `src/Orleans.Serialization/TypeSystem/TypeConverter.cs`
19. `src/Orleans.Core/GrainReferences/GrainReferenceActivator.cs`

如果你只想抓最关键的七个文件，那就是：

- `CodeGenerator.cs`
- `Annotations.cs`
- `GrainReference.cs`
- `ProxyGenerator.cs`
- `InvokableGenerator.cs`
- `SerializerGenerator.cs`
- `MetadataGenerator.cs`

---

## 26. 一句话收尾

把这条编译期链路压成一句话，就是：

Orleans 不是分别生成“代理”“请求对象”“序列化器”三堆孤立代码，而是先把接口和类型扫描成一份 `MetadataModel`，再同时生成 `Proxy_*`、`Invokable_*`、`Codec_*`、`Copier_*`、`Activator_*` 和 `Metadata_*`，最后通过 assembly attribute 和 `TypeManifestOptions` 把这些产物重新喂给运行时。

这就是 Orleans 能把“一个普通接口方法”做成强类型远程调用的真正底盘。
