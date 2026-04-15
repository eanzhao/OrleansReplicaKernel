# 透明 Observer 引用与 Object Reference 边界（第六十篇）

## 1. 这一篇补哪块

前一篇已经把 callback target 这条链接上了：

- source node 注册本地 callback target
- 远端 grain 拿到 callback handle
- grain 再通过 runtime 发回调 invocation
- source node 本地 callback registry 执行真实对象

这已经把“反向调用也走消息系统”这件事立住了。

但从 API 形状上看，它还偏底层。

因为使用方看到的还是：

- callback target 注册
- 裸 handle
- grain 方法参数里显式传 handle

而 Orleans 真正更像的形态是：

`observer reference / object reference`

也就是调用方看到的是一个强类型引用，而不是“我要手工管理一段 callback id”。

## 2. 为什么透明引用不是糖衣

这件事很容易被误解成只是 API 包装。

其实不是。

透明 observer reference 至少在 runtime 里解决三件事：

1. 它把 callback id 封进一个强类型引用对象里
2. 它让 grain 代码看到的是 `IEchoObserver`，而不是某种内部 handle
3. 它逼 runtime 明确回答一个问题：

`这个引用在不同 node、不同执行上下文里被调用时，到底该由谁发消息`

这第三点才是最关键的。

## 3. 最危险的错觉：把 observer reference 当普通本地对象

如果 observer reference 只是一个普通对象，里面偷偷抓着“最初创建它时的 runtime”，那一旦它被传到远端 grain 里再调用，就会发生一个非常隐蔽的身份错乱：

1. observer reference 是在 source node 创建的
2. 远端 grain 收到它
3. grain 在 node-b 上调用 `observer.OnXxxAsync`
4. 结果真正发消息的却还是 node-a 的 runtime

这时调用虽然能“跑”，但调用身份已经是错的：

- source/target 会倒过来
- response correlation 会变味
- 后续做 callback sequencing、callback metrics、client-side observer lifecycle 都会很脏

所以透明引用的难点不是“做个 proxy”，而是：

`proxy 在执行时要选对 runtime`

## 4. 当前阶段最小但正确的做法

这版 `OrleansReplicaKernel` 里，observer reference 先收成一个很小但语义对的模型：

1. observer reference 本质上还是一个 typed proxy
2. proxy 内部保留一个 fallback runtime
3. 但真正执行方法时，优先取当前 activation 执行上下文里的 runtime
4. 只有在 activation 上下文外部直接调用这个 reference 时，才退回 fallback runtime

也就是说，这个选择逻辑是：

`ActivationExecutionContext.CurrentRuntime ?? fallbackRuntime`

这样就能保证：

- source 端自己调它时，有可用 runtime
- 远端 grain 调它时，消息会从“当前 grain 所在 node”发出

这才是真正像 object reference 的语义。

## 5. 这一层和前一篇 callback target 的关系

可以把两篇理解成两层：

### 5.1 callback target layer

解决的是：

- 本地真实对象放哪儿
- 怎么注册 / 注销
- router 怎么把 callback id 送回正确 node

### 5.2 transparent reference layer

解决的是：

- 调用方和 grain 代码看到什么 API
- callback invocation 到底由哪个 runtime 发起

前一层不做，这一层没落点。
后一层不做，前一层就始终只是内部基础设施。

## 6. 这一版怎么落

当前这版里，这条线先落成了：

1. `EchoObserverReference`
   - 是一个强类型 `IEchoObserver` 引用
2. host 提供 observer reference 创建 API
   - 调用方拿到的是 `IEchoObserver`，不是裸 handle
3. `EchoGrain.PingWithObserverAsync(...)`
   - 参数类型已经是 `IEchoObserver`
4. `EchoObserverReference.OnEchoAsync(...)`
   - 调用时优先使用当前 activation runtime
   - 没有 activation 上下文时才使用 fallback runtime

这样 grain 端代码已经变成了更像 Orleans 的形状：

`await observer.OnEchoAsync(...)`

而不是：

`拿一个 callback handle，再手工从当前 runtime 发消息`

## 7. 这里最重要的一条边界

即便 observer reference 已经长成强类型 proxy，这一版也不能假装“object reference 序列化已经做完了”。

现在这版实际发生的是：

- observer reference 对象本体作为参数被直接带过去
- 它内部还保留了 identity 和 fallback runtime

这对同进程多节点模拟是够用的，但它不是最终版 object reference serialization。

真正完整复刻 Orleans 时，这一层迟早要拆成：

1. object reference 的 wire representation
2. runtime 里的 object reference codec / resolver
3. target 收到以后再按当前 runtime 重建 proxy

所以这一版做的是：

`透明 API 先长出来`

还不是：

`真正的 object reference serialization protocol 已经完成`

## 8. 最值得跑的验证

当前阶段最值得看的还是两条：

### 8.1 remote grain 调 typed observer reference

也就是：

1. source 创建一个 `IEchoObserver` reference
2. grain owner 在远端
3. grain 里直接 `await observer.OnEchoAsync(...)`
4. source 本地 observer 真的收到消息

这条验证的是：

`typed API 没有破坏 callback 消息语义`

### 8.2 observer reference disposed 后继续调用

也就是：

1. source 创建 observer reference
2. 拿到一个旧 reference
3. source 显式释放它
4. grain 再用旧 reference 去调
5. runtime 明确失败

这条验证的是：

`透明 reference 只是 API 变好了，不代表生命周期边界被抹平了`

## 9. 这一版故意还没做什么

这一版还没做：

1. 泛型化的 observer reference codegen
2. 真正的 object reference serializer / codec
3. callback reference checkpoint / recovery
4. 跨进程 transport 上的 object reference rehydration
5. client-side observer registry
6. observer reference lease renewal / heartbeat
7. object reference 的能力边界统一抽象

所以这一篇补的是：

`让 API 先长得像 observer reference`

而不是宣称 object reference 整套协议已经做完了。

## 10. 为什么完整复刻 Orleans 必须继续往这边走

如果系统最后只停在“显式 callback target + 裸 handle”这一层，那它虽然能工作，但还是更像内部原语，不像 Orleans 面向用户暴露的能力形态。

Orleans 真正有价值的一点，就是它把很多 runtime 复杂性藏在 reference abstraction 下面：

- grain reference
- observer reference
- object reference

所以完整复刻 Orleans，最终不能只把底层能力补齐，还得把这些能力抬成合适的抽象。

当前这版 transparent observer reference，就是沿着这条方向迈出的第一步：

`先让 API 长得对，再逐步把真正的 wire protocol 和序列化边界补上。`
