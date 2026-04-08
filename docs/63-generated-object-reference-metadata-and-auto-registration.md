# 63. Generated Object Reference Metadata 与 Auto Registration

## 1. 这一篇补哪块

前一篇已经把 object reference 的创建和重建收进了统一 registry。

但那一版还有个很明显的早期实现味道：

- `runtime` 已经知道 object reference 应该统一注册
- `host` 也已经知道应该从 registry 创建 typed reference
- 可真正装配时，`Program` 和测试还得手工写一行：
  - `AddObjectReference<IEchoObserver>(...)`

这说明运行时边界已经有了，元数据边界却还没补上。

如果继续这样长下去，后面每多一个 observer / object reference，都要在宿主装配代码里手写一次注册。那最后得到的仍然不是 Orleans 那种“生成代码把运行时能发现的信息带回来”，而只是把 lambda 从 invokable 挪到了 builder。

所以这一篇补的是下一步：

`generated object reference 自己带元数据，builder 再按程序集扫描并自动注册`

## 2. 为什么手工 AddObjectReference 不能作为长期方案

手工注册当然能跑，但它有几个很快就会放大的问题：

1. proxy 类型知识仍然泄漏在应用装配层
2. 生成代码和运行时之间没有正式 metadata 契约
3. 只要跨多个程序集，装配代码就会越来越像人工维护的白名单
4. 很难继续往真正的 codegen / application part 方向推进

完整复刻 Orleans，迟早要把这件事改成：

- generated code 声明“我代表哪个接口”
- builder/runtime 负责发现和接入

而不是：

- 应用入口人工记住每一个 generated reference

## 3. 当前这版到底做了什么

当前这版先落了一个很窄、但方向正确的版本：

1. 新增 `GeneratedObjectReferenceAttribute`
   - 让 generated reference 显式声明自己服务哪个接口
2. `EchoObserverReference.g.cs`
   - 现在带上了这份 attribute
3. `OrleansReplicaKernelBuilder.AddGeneratedObjectReferencesFromAssembly(...)`
   - 允许宿主把某个程序集交给 builder 扫描
4. builder 在 `Build(...)` 前统一扫描
   - 找出所有带 `GeneratedObjectReferenceAttribute` 的 concrete type
5. 扫描时做最小合法性检查
   - 必须实现 `IObjectReference`
   - 必须实现 attribute 指向的接口
   - 必须暴露 `(IInvocationRuntime, GrainId)` 构造函数
6. 扫描通过后，builder 自动把它注册进 `ObjectReferenceFactoryRegistry`

这样，应用层终于不需要再手工写：

- `IEchoObserver -> EchoObserverReference`

这类映射了。

## 4. 这层抽象真正解决了什么

这一轮最重要的，不是少写一行注册代码，而是把 object reference 的“发现来源”改了。

之前是：

- 应用代码手工知道 generated proxy 类型

现在变成：

- generated proxy 自己带 metadata
- builder 只负责扫描程序集并收进 registry

这两者差别很大。

前者仍然是人工胶水。
后者才开始像一个正式 runtime：

- codegen 产物带回元数据
- runtime 装配层消费元数据

这才是后面继续往 serializer、metadata manifest、application part 风格发现推进的正确方向。

## 5. 为什么这一步很适合作为当前阶段的落点

现在直接做完整 application part / manifest 系统还太早，因为：

- grain codegen 自动发现还没彻底补全
- serializer/codec metadata 还没真正接进来
- 多程序集聚合和版本冲突策略也还没正式设计

但如果完全不做元数据发现，又会一直停在：

- “运行时边界看起来有了”
- “真正装配还是靠人工点名”

所以这个阶段最合适的做法，就是先补：

- `attribute`
- `assembly scan`
- `auto registration`

先把元数据这条线接上，再往更完整的 application part 体系推进。

## 6. 当前版本的边界还停在哪里

这一版仍然只是第一层，不是最终形态。

还没做到的包括：

1. 自动扫描所有 grain / serializer / object reference 生成产物
2. 统一的 metadata manifest
3. application part 风格的多程序集聚合
4. 冲突解析、版本选择、模块裁剪
5. 真正跨进程的 object reference serialization contract

所以这篇不能理解成“object reference 发现系统已经完整了”，更准确的说法是：

`builder 已经不再依赖手工逐个注册 generated object reference，而是开始消费生成代码自己带回来的元数据。`

## 7. 对完整复刻 Orleans 的意义

完整复刻 Orleans，最怕的一种退化是：

- 核心边界看起来都很干净
- 真正一到装配层，又全靠手工 glue code 黏起来

那样最后只会得到一个“局部实现还不错、系统装配却越来越散”的 runtime。

这次补的这层很小，但意义不小：

- generated code 不再只是“帮你少写一点样板代码”
- 它开始承担“把运行时需要知道的事实带回来”这件事

一旦这件事成立，后面再补：

- grain metadata
- serializer metadata
- provider / capability metadata

才会有同一条清晰路线，而不是每一类能力都各搞一套注册办法。
