# 61. Object Reference Representation 与 Rehydration

## 1. 这一篇补哪块

前一篇已经把 observer reference 的 API 形状抬起来了：

- 调用方拿到的是强类型 `IEchoObserver`
- grain 里直接 `await observer.OnEchoAsync(...)`
- 调用时优先使用当前 activation 的 runtime

但如果做到这里就停，系统其实还留着一个很关键的问题：

`observer reference 到底是“对象本体过边界”，还是“引用表示过边界”？`

这两件事差别非常大。

## 2. 为什么“对象本体直接传过去”不够

在同进程多节点模拟里，最容易让人产生一种错觉：

- 反正都在一个进程里
- 把 `EchoObserverReference` 对象本体塞进消息里
- 目标端再直接拿这个对象调用
- 看起来也能跑

但这只是模拟环境下的侥幸，不是 runtime 语义。

因为一旦往真正完整复刻 Orleans 继续走，后面迟早要面对这些事实：

1. 消息会跨进程
2. 对象本体不会真的跟着消息走
3. 能跟着消息走的，只能是某种引用表示
4. 目标端必须根据这份表示重新长出一个本地 proxy

所以：

`object reference 的本质不是“把 proxy 带过去”，而是“把 proxy 的可重建表示带过去”`

## 3. 这一层真正要解决什么

这层最核心的不是序列化技术细节，而是边界表达：

### 3.1 source 侧

source 侧要能把一个 object reference 降成一份最小表示。

至少要表达：

- 它对应哪个接口
- 它的对象 id 是什么

### 3.2 target 侧

target 侧要能根据这份表示重新长出一个新的、可调用的 reference。

重点是：

- 这不是把 source 那个对象搬过来
- 这是在 target 本地重建一个等价 proxy

### 3.3 边界校验

runtime 还要明确拒绝一类错误：

`你传进来的只是一个普通本地实现，不是 runtime object reference`

否则同进程环境里很容易继续偷偷走“对象直带”的老路。

## 4. 当前阶段最小但正确的模型

到这一步，最值得先做的是三件事：

1. 定义 `IObjectReference`
2. 定义 `ObjectReferenceData`
3. 定义最小的 `Export / Rehydrate`

也就是：

- source 侧把 typed reference 导出成 `ObjectReferenceData`
- target 侧再把 `ObjectReferenceData` 重建成 typed reference

这个模型还不是完整 Orleans 的 serializer/codec 体系，但已经把最重要的事实表达出来了：

`消息里过边界的不是 proxy 对象，而是 proxy 的表示`

## 5. 为什么要有 `IObjectReference`

如果没有一个显式 marker，runtime 根本分不清这两种东西：

1. 这是 runtime 管的 object reference
2. 这只是某个普通本地对象，刚好实现了同一个接口

这两者在 C# 类型系统里都可能长得像 `IEchoObserver`。

所以 runtime 需要一个更底层的判断标准：

`是不是 runtime object reference`

`IObjectReference` 在当前阶段做的就是这件事。

它不负责所有能力，它只是先把边界画出来。

## 6. 为什么 `ObjectReferenceData` 至少要带接口名

只带一个 `GrainId` 不够。

因为 target 侧收到这份表示以后，还需要确认：

- 我要把它重建成什么接口的 proxy
- 当前 invokable 期待的接口和这份表示里声明的是不是一致

如果这一步不校验，就会出现很脏的情况：

- 一份 callback id 被错误地当成别的接口重建
- 结果错误在更晚、更深的地方才爆出来

所以第一版就应该至少保留：

- `InterfaceName`
- `GrainId`

## 7. 这一版怎么落

当前这版 `OrleansReplicaKernel` 里，这条线收成了：

1. `IObjectReference`
   - 说明这个对象是 runtime 管理的 reference
2. `ObjectReferenceData`
   - 是最小引用表示
3. `ObjectReferenceSerializer.Export<TInterface>(...)`
   - source 侧把 typed reference 导出成表示
4. `ObjectReferenceSerializer.Rehydrate<TInterface>(...)`
   - target 侧根据表示和当前 runtime 重建 typed proxy
5. `EchoPingWithObserverInvokable`
   - 不再把 `IEchoObserver` 对象本体存进 invokable
   - 而是把它先降成 `ObjectReferenceData`
   - 到 target 侧再还原成 `EchoObserverReference`

这就把 observer reference 从：

`同进程里对象刚好还能用`

推进到了：

`即便未来真的跨边界，也已经是按“表示 -> 重建”这条路在长`

## 8. 这一版故意补的一个失败场景

当前实现里，我建议一定要明确拒绝这一类调用：

- 你直接 new 一个普通 `RecordingEchoObserver`
- 它只是实现了 `IEchoObserver`
- 但它不是 runtime object reference
- 然后你把它直接传给 grain

这件事在编译期不一定能挡住，因为签名上它确实符合 `IEchoObserver`。

所以第一版最合理的做法就是在导出边界上明确失败：

`this is not a runtime object reference`

这条失败路径很重要，因为它能强迫系统停止依赖“同进程对象直带”。

## 9. 这层和真正序列化体系的关系

这一版还是要把话说清楚：

当前做的是：

- object reference representation
- target-side rehydrate
- interface boundary check

还没做的是：

- 统一 codec/provider
- 真正的 wire format
- application part 里的 object reference type metadata
- 跨进程 transport 上的真正编码/解码

也就是说，当前这版不是“完整 object reference serialization 已完成”，而是：

`object reference 已经开始按可序列化表示的方向组织起来了`

## 10. 最值得跑的验证

这层最值得跑的其实是两条：

### 10.1 typed observer reference 仍然能远端回调成功

说明：

- export 没把 reference 语义弄丢
- rehydrate 之后的 proxy 还能正常工作

### 10.2 普通本地实现不能直接过 invocation boundary

说明：

- runtime 已经在边界上显式拒绝“裸对象越界”
- 当前实现不再偷偷依赖同进程对象本体传递

这两条同时成立，才说明这层真的从“对象直带”迈到了“引用表示”。

## 11. 为什么完整复刻 Orleans 必须走到这一步

只要目标是完整复刻 Orleans，object reference 就不可能永远停留在“本地 proxy 恰好可用”的状态。

因为 Orleans 真正要面对的是：

- client observer
- callback target
- extension
- 可能的系统 target / 反向目标

这些东西最终都要求 runtime 明确回答：

`引用如何表示，如何过边界，如何在目标端重建`

所以这一篇补的不是“更漂亮的 API”，而是把透明 reference 真正推到下一层：

`它不只是一个看起来像对象的代理，它开始拥有清晰的边界表示和重建语义了。`
