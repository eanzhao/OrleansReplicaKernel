# Object Reference 工厂注册表与生成式再水化（第六十二篇）

## 1. 这一篇补哪块

前一篇已经把 object reference 从“对象本体直接过边界”推进到了：

- source 侧导出 `ObjectReferenceData`
- target 侧再把它 rehydrate 成 typed reference

但那一版还有一个很明显的早期实现味道：

`rehydrate 还是由具体 invokable 自己手写 factory lambda`

比如：

- `EchoPingWithObserverInvokable`
- 自己知道要把 `ObjectReferenceData`
- 还原成 `EchoObserverReference`

这说明边界虽然已经有了，但还没抽象好。

因为完整复刻 Orleans，不可能让每个生成代码、每个 invokable、每个能力模块都各自记住：

`这个接口该用哪个 proxy factory 重建`

这件事迟早要回到一层统一注册表里。

## 2. 为什么不能让 invokable 永远手写 rehydrate lambda

如果继续沿着“每个 invokable 都自己传一段 factory lambda”往下长，会立刻出现几个问题：

1. 代码生成物和具体 proxy 类型强耦合
2. 同一个接口的重建逻辑会散落在多个 invokable 里
3. 后面一旦接入真正的 metadata / serializer，就不知道该以谁为准
4. object reference 能不能被创建，不再是 runtime 装配层的事实，而变成某个调用点的偶然行为

这跟前面几篇一直在做的事情是反着来的。

前面其实一直在把 Orleans 的很多“到处都能偷偷做”的事往运行时装配层收：

- grain reference factory
- directory / routing
- membership dissemination
- callback target registry

object reference rehydrate 也应该沿同一个方向收。

## 3. 当前阶段最合理的目标

这一层最值得先做到的是：

1. builder 能注册 object reference factory
2. runtime 持有一份 object reference factory registry
3. generated invokable 在 target 侧 rehydrate 时，只依赖 registry
4. host 创建 object reference 时，也优先走 registry，而不是让调用点再传 lambda

这四步一旦立住，object reference 这条线才算真正从“demo 代码能跑”变成“runtime 装配层有正式边界”。

## 4. 这层真正统一了哪两件事

### 4.1 source-side creation

调用方想创建一个 object reference 时，应该由 host/runtime 去决定：

- 这个接口有没有注册
- 对应 proxy 要怎么创建

而不该让调用方每次都手工传一段：

`(runtime, grainId) => new XxxReference(runtime, grainId)`

### 4.2 target-side rehydration

target 收到 `ObjectReferenceData` 以后，也应该问同一份 registry：

`这个接口的 reference factory 是谁`

而不是让每个 invokable 再各自 hardcode 一次。

source create 和 target rehydrate 如果不走同一套注册表，后面迟早会出现：

- source 能创建
- target 不能还原

或者反过来：

- target 硬编码还能还原
- 但 host 根本不承认这个接口是一个正式 object reference

## 5. 当前这版怎么落

当前这版 `OrleansReplicaKernel` 里，这条线先收成了：

1. `OrleansReplicaKernelBuilder.AddObjectReference<TInterface>(...)`
   - 注册 object reference factory
2. `ObjectReferenceFactoryRegistry`
   - 统一保存接口名到 reference factory 的映射
3. `InProcessRuntime`
   - 现在显式持有这份 registry
4. `IObjectReferenceRuntime`
   - 让 activation 执行期能通过当前 runtime 访问 registry
5. `OrleansReplicaKernelHost.CreateObjectReference<TInterface>(...)`
   - 创建 typed object reference 时直接走 registry
6. generated invokable
   - 在 target 侧 rehydrate 时也直接走 registry

这样 source create 和 target rehydrate 两边就终于对上了：

`一份注册，两个方向共用`

## 6. 为什么这比“看起来只是少传一个 lambda”更重要

表面上看，这一轮像是把：

- `CreateObserverReference(..., lambda)`

变成了：

- `CreateObjectReference<T>()`

但真正重要的不是 API 少传一个参数，而是：

`object reference factory 从调用点知识变成了 runtime 装配知识`

这会直接影响后面很多事情：

- metadata
- generated code
- serializer / codec
- host composition
- provider / application part 风格的发现与注册

如果这层不先收起来，后面越做越多，只会让 object reference 又重新散回各个角落。

## 7. 这一版最值得跑的两条验证

### 7.1 builder 注册后，host 可以直接创建 typed object reference

说明：

- object reference factory 已经正式进入装配层
- 调用点不再需要知道具体 proxy 类型

### 7.2 target-side rehydrate 不再依赖 invokable 自己的 hardcode lambda

说明：

- target 侧已经能从 runtime 提供的 registry 找到 factory
- generated invokable 只是声明“我要一个 `IEchoObserver`”，不再负责保存 factory 知识

这两条同时成立，才说明 object reference 这层开始有了真正的运行时抽象，而不再只是 demo glue code。

## 8. 当前版本故意还没做什么

这一版还没做：

1. 自动扫描 object reference metadata
2. codegen 自动发出 `AddObjectReference<T>()`
3. 统一 serializer/codec 层自动发现 object reference factory
4. application part 风格的 object reference registry 拼装
5. 多模块、多程序集下的 object reference factory 聚合

所以这一篇补的不是最终版 metadata 系统，而是先把这件事从“手工散点”收成“统一 registry”。

## 9. 为什么完整复刻 Orleans 迟早要走到这里

只要系统里开始出现多个 object reference 类型，runtime 就必须决定：

- factory 谁来注册
- 谁来发现
- 谁来重建

如果这些答案不在统一注册层里，最后一定会变成：

- 某些 reference 能在某些路径上工作
- 某些 reference 又只在某些 invokable 上工作
- 整个系统没有一份清晰的 truth

而完整复刻 Orleans 的目标，恰恰要求这些东西越来越统一、越来越能被运行时描述。

这一版的 `ObjectReferenceFactoryRegistry`，做的就是这一步：

`把 object reference 的创建和重建，第一次正式收进运行时注册表。`
