# 生成的 Grain Reference 元数据与契约绑定发现（第六十四篇）

## 1. 这一篇补哪块

63 把 object reference 这条线从手工注册推进到了：

- generated code 自己带 metadata
- builder 按程序集扫描
- runtime 自动注册 object reference factory

但 grain 这边其实还停在更早期的状态。

虽然 `EchoGrainReference`、`CounterGrainReference` 都已经是生成出来的 reference 壳了，可宿主装配时还得手工写：

- `IEchoGrain -> echo -> EchoGrainReference`
- `ICounterGrain -> counter -> CounterGrainReference`

这说明 grain reference 这条线的 codegen 已经有了，metadata discovery 却还没接上。

所以这一篇补的是：

`generated grain reference 自己声明 contract + grain type，builder 再按程序集把 contract binding 自动收进来`

## 2. 为什么 grain 这条线也必须补 metadata

如果 object reference 有 metadata discovery，grain reference 却还靠手工注册，最后会出现一种很奇怪的状态：

- observer / callback 走的是“生成代码带事实回来”
- grain contract 却还是“应用代码手工点名”

这会让整个 runtime 的装配边界很不统一。

更重要的是，完整复刻 Orleans 时，grain reference 不只是“帮你少写个代理类”，它还应该承担两件事：

1. 告诉运行时“这个 contract 对应哪个 grain type”
2. 告诉运行时“这个 contract 的 reference factory 在哪儿”

如果这两件事还靠应用层手工维护，后面再往：

- manifest
- application part
- 多程序集聚合

这些方向走时，grain 这条线就还是会拖后腿。

## 3. 这一版到底做了什么

当前这版先补了一个很窄、但方向正确的版本：

1. 新增 `GeneratedGrainReferenceAttribute`
   - generated reference 自己声明：
     - `ContractType`
     - `GrainType`
2. `EchoGrainReference.g.cs`
   - 现在带 `[GeneratedGrainReference(typeof(IEchoGrain), "echo")]`
3. `CounterGrainReference.g.cs`
   - 现在带 `[GeneratedGrainReference(typeof(ICounterGrain), "counter")]`
4. `OrleansReplicaKernelBuilder.AddGeneratedGrainReferencesFromAssembly(...)`
   - 宿主把程序集交给 builder 扫描
5. builder 在 `Build(...)` 前统一把这些 generated reference 收进 grain binding 表

这样 `Program` 和测试里就不再需要手工写：

- `(runtime, grainId) => new EchoGrainReference(runtime, grainId)`
- `(runtime, grainId) => new CounterGrainReference(runtime, grainId)`

## 4. 这一层为什么要故意和 grain implementation registration 分开

这次最重要的设计点，不是扫描 attribute 本身，而是：

`grain reference binding` 和 `grain implementation factory` 被明确拆成了两层。

现在 builder 里变成了两套注册：

1. `AddGeneratedGrainReferencesFromAssembly(...)`
   - 解决 contract -> grain type -> reference factory
2. `AddGrainImplementation(...)`
   - 解决 grain type -> activation instance factory

这两层不能混。

因为它们回答的是两个完全不同的问题：

- “拿到 `IEchoGrain`，我该返回哪个 reference？”
- “真正要激活 `echo` 时，我该 new 哪个 grain instance？”

Orleans 里这两件事后面都会进入更大的 metadata / manifest 体系，但在当前阶段，先把它们拆成两张表，比继续沿着一个 `AddGrain(...)` 大杂烩往下长要干净得多。

## 5. 为什么这比“只是改了 builder API”更重要

表面上看，这轮只是把：

- `AddGrain(contract, grainType, grainFactory, referenceFactory)`

拆成了：

- `AddGeneratedGrainReferencesFromAssembly(...)`
- `AddGrainImplementation(...)`

但真正发生变化的是：

`grain reference factory 不再是应用入口手工保存的知识，而是生成代码通过 metadata 带回来的知识。`

这意味着：

- `GetGrain<T>()` 用到的 contract binding，开始来自 generated metadata
- 应用层只需要补“当前运行时里有哪些 grain implementation 能激活”
- 后面再往 manifest 走时，grain reference 这条线已经有正确的生长方向

## 6. 当前这版故意还没做到什么

这一版仍然不是完整 grain metadata 系统。

还没做的包括：

1. grain implementation 的自动发现和 activator metadata
2. grain properties / placement / collection age / reentrancy 等 metadata 汇总
3. 统一 manifest provider
4. application part 风格的 assembly graph 传播
5. grain reference、serializer、object reference、provider metadata 的统一聚合

所以现在更准确的理解应该是：

`contract binding discovery 已经自动化了，但 activation side 的实现注册仍然是手工的。`

## 7. 为什么这一步对完整复刻 Orleans 很关键

完整复刻 Orleans，不是把一堆生成类抄出来就够了，关键是：

- 生成类怎么把事实带回 runtime
- runtime 又怎么消费这些事实

这次 grain reference 这条线补上以后，现在已经有两条路开始长成同一个方向了：

1. object reference
2. grain reference

它们都开始具备同一种结构：

- generated type
- attribute metadata
- builder assembly scan
- runtime registration table

这条结构一旦形成，后面再补：

- invokable metadata
- serializer metadata
- provider metadata

就不需要每一类能力都重新发明一套装配办法。

## 8. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的说法是：

`OrleansReplicaKernel 已经不再手工维护 grain contract 到 generated reference 的映射，而是开始从 generated metadata 自动恢复这层 binding；但 grain implementation 的激活侧注册，还故意保留在手工层，等待后面的更完整 manifest 设计。`
