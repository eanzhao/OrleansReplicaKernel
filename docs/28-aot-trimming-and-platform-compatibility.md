# AOT、裁剪与平台兼容：Orleans 到底能帮你多少，又卡在哪些硬边界（第二十八篇）

这一篇只讲一件事：Orleans 在 AOT 和 IL trimming 这件事上，到底是“更友好”了，还是“只是把一些反射往前挪了”。

先把结论说直白一点：

- Orleans 现在比早期版本更适合做 AOT 和 trimming 友好型应用，主要靠 source generator、manifest、generated serializer / proxy / invokable 把一大块运行时反射前移到了编译期。
- 但它不是一个完全静态的系统。运行时里还留着不少 `Reflection.Emit`、`Assembly.Load`、`GetCustomAttributes`、`GetMethods`、`MakeGenericType`、`Activator.CreateInstance` 之类的动态入口。
- 也就是说，source generator 帮了很大忙，但它没有把 Orleans 变成“纯静态、纯 AOT”的框架。
- 真正的硬边界主要集中在三类地方：运行时扫描和装配、动态实例化和委托生成、以及按类型名或属性做的反射发现。
- 如果你要把 Orleans 往 NativeAOT 或更激进的裁剪环境里推，最先要盯住的不是业务 grain，而是序列化、宿主装配、provider 发现和一些测试/辅助路径。

如果把这一篇压成一句话，就是：

> Orleans 已经尽量把“会爆裁剪的活”前移到编译期了，但它骨架里仍然有几条动态链，AOT 只能靠约束和约定去贴近，不能靠一句“已经支持”就当成彻底静态。

---

## 1. 先把整条主链压成一张图

```text
编译期
  -> Microsoft.Orleans.Sdk / Orleans.CodeGenerator
  -> 扫描接口、DTO、属性、application part
  -> 生成 Proxy_* / Invokable_* / Codec_* / Copier_* / Activator_* / Metadata_*
  -> 产出 TypeManifestProviderAttribute / ApplicationPartAttribute

启动期
  -> AddSerializer / AddOrleans / UseOrleans
  -> ReferencedAssemblyProvider / AssemblyLoadContext / GetCustomAttributes
  -> TypeManifestOptions
  -> CodecProvider / TypeConverter / TypeCodec

运行期
  -> GrainReferenceActivator / GrainContextActivator
  -> CodecProvider.MakeGenericType / specializable codec / dynamic activator
  -> SerializationConstructorFactory / SerializationCallbacksFactory / FieldAccessor
  -> Reflection.Emit / DynamicMethod / Activator.CreateInstance / MethodInfo 查找

裁剪与 AOT 风险点
  -> 反射发现不到类型
  -> 动态生成委托/IL 在 NativeAOT 下不成立
  -> 没被 manifest 或 application part 根住的类型被裁掉
  -> runtime 扫描失败后，proxy / serializer / provider 找不到实现
```

这张图里最关键的一点是：

Orleans 的 AOT 兼容不是靠“完全不反射”，而是靠“把绝大部分必须知道的类型关系，提前生成并显式登记”。

所以它更像是一个“静态优先、动态兜底”的系统。

---

## 2. source generator 具体帮了什么

关键文件：

- `src/Orleans.CodeGenerator/OrleansSourceGenerator.cs`
- `src/Orleans.CodeGenerator/CodeGenerator.cs`
- `src/Orleans.CodeGenerator/ProxyGenerator.cs`
- `src/Orleans.CodeGenerator/InvokableGenerator.cs`
- `src/Orleans.CodeGenerator/SerializerGenerator.cs`
- `src/Orleans.CodeGenerator/MetadataGenerator.cs`
- `src/Orleans.CodeGenerator/ApplicationPartAttributeGenerator.cs`

### 2.1 它把最贵的几类反射搬到了编译期

source generator 最有价值的地方，不是“生成了很多代码”，而是它提前把下面这些关系定死了：

- grain interface -> proxy
- method signature -> invokable
- DTO -> serializer / copier / activator
- assembly -> metadata provider

这几件事一旦在编译期完成，运行时就不用再靠“扫程序集找接口、找属性、找方法、拼字符串、猜类型”来做同一件事。

这对 trimming 非常重要，因为裁剪最怕的就是运行时发现类型关系，只要一条链找不到，整个功能就崩。

### 2.2 `ApplicationPartAttribute` 和 `TypeManifestProviderAttribute` 是关键接线口

生成器最后会把两类东西再塞回运行时：

- `ApplicationPartAttribute`
- `TypeManifestProviderAttribute`

前者告诉运行时“哪些程序集是 Orleans 的应用部件”。

后者告诉运行时“这个程序集对应的 manifest provider 在哪儿”。

这意味着 Orleans 不是靠运行时自己发现所有东西，而是靠编译期先把清单写出来，再让启动时读清单。

这条路比纯反射更适合裁剪，但它依然要求：

- 你的生成链是完整的
- 你的程序集能被根住
- 你的 application part 没被 linker 误删

### 2.3 source generator 也在帮 analyzer 提前报错

`SerializerConfigurationAnalyzer` 和 `SerializerConfigurationValidator` 会提前告诉你：

- grain interface 里引用的类型有没有 serializer
- 有没有 copier
- 哪些类型在配置里看起来会出问题

这对 AOT 很有帮助，因为很多裁剪问题不是运行时才炸，而是“某个类型没被 root 住”，提前分析就能发现一半。

不过这里要注意：

analyzer 只能发现“显式配置里能看到的问题”。

它救不了运行时临时拼出来的那种类型路径。

---

## 3. 运行时还在靠哪些动态能力

关键文件：

- `src/Orleans.Serialization/Activators/DefaultActivator.cs`
- `src/Orleans.Serialization/ISerializableSerializer/SerializationCallbacksFactory.cs`
- `src/Orleans.Serialization/ISerializableSerializer/SerializationConstructorFactory.cs`
- `src/Orleans.Serialization/Utilities/FieldAccessor.cs`
- `src/Orleans.Serialization/Hosting/ReferencedAssemblyProvider.cs`
- `src/Orleans.Serialization/TypeSystem/CachedTypeResolver.cs`
- `src/Orleans.Core/GrainReferences/GrainReferenceActivator.cs`
- `src/Orleans.Runtime/Activation/IGrainContextActivator.cs`
- `src/Orleans.Runtime/Facet/Persistent/PersistentStateAttributeMapper.cs`

### 3.1 `Reflection.Emit` 还是在

这几个类是最明显的 AOT 硬边界：

- `DefaultActivator<T>`
- `SerializationCallbacksFactory`
- `SerializationConstructorFactory`
- `FieldAccessor`

它们都在用 `DynamicMethod` 或等价的动态 IL 生成。

这意味着只要这条路径真的跑起来，你就不能把它当成“纯 NativeAOT 友好”。

它们的用途都很实际：

- 给默认构造函数、序列化构造函数、字段访问器、回调方法做高性能 trampoline
- 少掉一层反射调用开销

但代价就是平台兼容性差。

### 3.2 `Assembly.Load` 和 `GetCustomAttributes` 仍然很重

`ReferencedAssemblyProvider` 会：

- 扫 `AppDomain.CurrentDomain.GetAssemblies()`
- 读 `ApplicationPartAttribute`
- 通过 `Assembly.Load(...)` 递归加载相关程序集

`CachedTypeResolver` 也会：

- 扫当前 AppDomain 里的程序集
- 试 `Type.GetType(...)`
- 必要时再 `Assembly.Load(...)`

这些逻辑对 trimming 很敏感。

因为裁剪环境下，程序集和类型如果没有被显式 root，运行时扫不到就是扫不到。

### 3.3 `Activator.CreateInstance` 还到处都是

你在仓库里能直接看到不少动态实例化：

- `DefaultSiloServices` / `DefaultClientServices` 里的 provider 和 configurator 创建
- `GrainReferenceCodecProvider`
- `DefaultStreamNamespacePredicateProvider`
- 一些 transaction / streaming / testing 路径里的 factory

在普通 JIT 环境里这没什么。

但在更激进的 AOT 或裁剪场景里，这种“拿到 `Type` 再直接 `CreateInstance`”的写法，几乎都需要额外的根保持和类型可达性保证。

### 3.4 `GetMethod` / `GetInterfaceMap` / `MethodInfo` 依赖也没少

Orleans 在运行时仍然大量使用：

- `GetMethod`
- `GetMethods`
- `GetInterfaceMap`
- `MethodInfo`
- `MakeGenericType`

它们主要分布在：

- grain interface 到 implementation 的映射
- invokable / method lookup
- `MayInterleave`、transaction、timer、storage callback 等特殊调用链

这些都说明 Orleans 不是“只靠生成代码就完全不看反射”的框架。

它只是把最常见的那部分路径尽量生成掉了。

---

## 4. 哪些模块更依赖动态能力

这部分我建议直接按风险从高到低看。

### 4.1 序列化 activator / callback / field accessor

最不 AOT 的一组：

- `SerializationConstructorFactory`
- `SerializationCallbacksFactory`
- `FieldAccessor`
- `DefaultActivator<T>`

它们直接依赖动态 IL。

如果你真的要推 NativeAOT，这一块通常是第一批要重写或替代的。

### 4.2 runtime 的反射发现与 provider 装配

第二组风险很高：

- `ReferencedAssemblyProvider`
- `DefaultSiloServices`
- `DefaultClientServices`
- `SerializerBuilderExtensions`
- `CachedTypeResolver`

这组不是因为功能坏，而是因为它们默认假设：

- 程序集能被枚举
- 属性能被读到
- Type 能被解析出来

trim 一旦把这条路剪断，整条 provider / manifest 装配链就会掉。

### 4.3 grain / transaction / streaming 的特殊反射路径

这一组属于“能跑，但不算干净”：

- `GrainReferenceActivator`
- `IGrainContextActivator`
- `PersistentStateAttributeMapper`
- `TransactionalStateAttributeMapper`
- streaming 的 predicate / generator factory

这些地方大量依赖 `Type` 和 `MethodInfo`，而且很多是为了兼容属性配置、泛型工厂、运行时扩展点。

### 4.4 测试和调试路径

像 `TestClusterHostFactory`、一些测试 helper、诊断分析工具，平台兼容性都不是第一优先级。

这类代码在生产 AOT 方案里通常要么不带，要么单独隔离。

---

## 5. 哪些地方 source generator 真的帮上了忙

### 5.1 grain proxy 和 invokable

这是 Orleans 最值钱的部分。

代理和 invokable 一旦生成出来，运行时就不用再去“猜接口有哪些方法、方法 id 是什么、返回值怎么处理”。

这直接减少了：

- 反射扫描
- MethodInfo 搜索
- runtime 代码拼装

### 5.2 serializer / copier / activator

`[GenerateSerializer]` 和相关生成器让绝大多数 DTO 都能提前落地。

这对 trimming 特别关键，因为对象序列化最怕运行时发现“这个字段类型没保住”。

### 5.3 manifest / application part

`Metadata_*` + `TypeManifestProviderAttribute` 把很多 runtime discover 的事变成显式配置。

这对 AOT 友好，因为 linker 更容易看见“谁依赖谁”。

但它只对已经被生成出来的类型链有效。

只要你还是让运行时自己去加载插件式程序集，那条链就还是动态的。

---

## 6. 平台兼容的硬边界在哪里

这块我觉得最重要。

### 6.1 宿主层的边界

宿主层最大的边界不是 `HostBuilder`，而是：

- 扫程序集
- 找 application parts
- 找 manifest providers
- 反射创建 provider

这意味着，宿主层越像“自动发现框架”，裁剪就越吃力。

如果你的部署模型要求插件式扩展、动态装配、运行时加载，那 AOT 基本只能走保守方案。

### 6.2 序列化层的边界

序列化层的硬边界是 `Reflection.Emit`。

只要还保留动态 constructor / callback / field accessor 的 trampoline，NativeAOT 就不会轻松。

你可以把大头生成掉，但这些尾巴不一定能全砍干净。

### 6.3 codegen 层的边界

codegen 本身已经比 runtime 反射友好了很多。

但它仍然假设：

- 编译时能看到足够多的类型
- source generator 能完整跑
- 产物对应的程序集会被打进最终应用

只要你把某些模块拆成运行时才加载的插件，source generator 也帮不了太多。

### 6.4 反射数据本身也得保住

像 `GetCustomAttributes`、`GetMethods`、`GetInterfaceMap` 这种路径，如果对应的成员在 trim 后没了，结果就是：

- manifest 配不出来
- serializer/analyzer 看不到
- invokable / extension / provider 解析失败

所以 AOT 友好不只是“别写动态 IL”，还要“别让 linker 把你依赖的成员裁掉”。

---

## 7. 如果你要自己复刻，我会怎么拆

如果目标是“复刻一版更适合 AOT 的 Orleans”，我会先把这一层拆成四块。

### 7.1 生成期清单层

把这些都统一交给编译期：

- proxy
- invokable
- serializer
- copier
- activator
- type manifest

不要让 runtime 再去猜。

### 7.2 运行时装配层

把反射发现、程序集扫描、provider 装配收口成一个明确的入口。

这样你能更清楚地标记：

- 哪些程序集必须 root
- 哪些特性是可选的
- 哪些能力在 AOT 模式下直接关闭

### 7.3 动态能力隔离层

把 `Reflection.Emit`、`Assembly.Load`、`Activator.CreateInstance` 这类代码隔离到单独层。

然后在 AOT 模式下用替代实现或直接禁用。

### 7.4 兼容分析层

把 analyzer 从“建议工具”提升成“构建期约束”。

让缺 serializer、缺 copier、缺 manifest、缺可达成员这类问题在编译期就能挡住，而不是让运行时去试错。

---

## 8. 这一篇最该反复看的源码文件

建议按这个顺序读：

1. `src/Orleans.CodeGenerator/OrleansSourceGenerator.cs`
2. `src/Orleans.CodeGenerator/CodeGenerator.cs`
3. `src/Orleans.CodeGenerator/SerializerGenerator.cs`
4. `src/Orleans.CodeGenerator/ProxyGenerator.cs`
5. `src/Orleans.CodeGenerator/InvokableGenerator.cs`
6. `src/Orleans.Serialization/Hosting/ReferencedAssemblyProvider.cs`
7. `src/Orleans.Serialization/Hosting/SerializerBuilderExtensions.cs`
8. `src/Orleans.Serialization/Activators/DefaultActivator.cs`
9. `src/Orleans.Serialization/ISerializableSerializer/SerializationCallbacksFactory.cs`
10. `src/Orleans.Serialization/ISerializableSerializer/SerializationConstructorFactory.cs`
11. `src/Orleans.Serialization/Utilities/FieldAccessor.cs`
12. `src/Orleans.Serialization/GeneratedCodeHelpers/OrleansGeneratedCodeHelper.cs`
13. `src/Orleans.Core/GrainReferences/GrainReferenceActivator.cs`
14. `src/Orleans.Runtime/Activation/IGrainContextActivator.cs`
15. `src/Orleans.Core/Configuration/Validators/SerializerConfigurationValidator.cs`

如果把这 15 个文件过一遍，你会很清楚地看到：

Orleans 的 AOT 兼容是“编译期尽量静态化，运行时保留少量动态口子”，不是“已经彻底静态化”。

---

## 9. 这一篇的落点

把这一篇压成一句话，就是：

Orleans 对 AOT 和 trimming 的态度不是“完全避免反射”，而是“把最容易出问题的路径前移到 source generator 和 manifest，再把剩下的动态口子尽量收敛在少数模块里”。

这套做法已经比纯反射框架好很多了，但它仍然有硬边界：

- 动态 IL 不适合 NativeAOT
- 运行时扫描不适合激进裁剪
- 插件式装配会放大 linker 风险
- 只靠 source generator 不能消灭所有动态能力

所以如果你要复刻一版更干净的 Orleans，我的建议很直接：

先把静态链做满，再决定哪些动态能力允许留下。

不要反过来。
