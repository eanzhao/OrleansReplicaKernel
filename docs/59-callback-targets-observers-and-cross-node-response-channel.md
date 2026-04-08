# 59. Callback Targets、Observers 与 Cross-Node Response Channel

## 1. 这一篇补哪块

前面几篇把普通 grain 调用这条主线已经收得比较完整了：

- source 发 request
- target 执行
- response 回 source
- source 再做 timeout / retry / dedupe / stale / duplicate 分类

但 Orleans 里还有另一条同样重要的线：

`grain 反过来回调调用方持有的对象`

这条线在 Orleans 里通常会落到这些概念上：

- observer
- object reference
- callback target

如果完整复刻 Orleans，runtime 不能只会：

`caller -> grain`

它还得会：

`grain -> callback target`

而且这条回调链不能偷懒直调本地对象，不然前面做的 message、routing、response channel 语义就全绕开了。

## 2. 为什么 callback target 不能伪装成普通 grain

callback target 和 grain 看起来都像“有个 id，可以调方法”，但它们不是一回事。

grain 的核心语义是：

- 有 placement
- 有 owner
- 有 activation 生命周期
- 可能 handoff
- 可能 checkpoint metadata

callback target 的核心语义不是这些。

它更像：

- 由某个 source node 本地注册出来
- 只活在那个 node 上
- 不参与 placement
- 不参与 handoff
- 不进 checkpoint
- 生命周期通常比 grain 更短，而且由注册方显式控制

所以它可以复用 message 和 invocation 机制，但不该在语义上混成普通 grain。

## 3. 当前阶段最小但真实的做法

在 `OrleansReplicaKernel` 当前阶段，最值得先做的是一版“显式 callback target handle”，而不是一步到位造完整透明 object reference API。

也就是：

1. source node 本地注册一个 callback target
2. runtime 给它分配一个 callback id
3. grain 收到的是一个 callback handle
4. grain 在自己的 activation 上下文里，用当前 runtime 去调用这个 handle
5. router 根据 callback id 直接把请求路由回注册它的 node
6. source node 的 local callback registry 负责找到真实对象并执行

这条线虽然还不是最终形态的“完全透明 observer reference”，但它已经把最关键的 runtime 语义立住了：

`callback 也是消息，不是内存直调`

## 4. 这里最容易犯的一个错

如果回调对象只是一个普通 proxy，而且 proxy 里固化的是“创建它的那个 runtime”，那远端 grain 调它时就会出现一个很隐蔽的问题：

- 调用看起来发生在 node-b 的 grain 里
- 但真正发消息的却是 node-a 的 runtime

这样 source/target 身份马上就糊了。

所以当前这版实现里，grain 不是拿着“可直接调用的 observer proxy”去回调，而是拿着一个显式 handle，再通过当前 activation 执行上下文里的 runtime 发起调用。

也就是说，出站 callback 的 node 身份来自：

`当前正在执行的 grain activation`

而不是来自“这个 handle 最初在哪儿创建”。

## 5. Callback target 这层到底需要什么

这一层真正需要的是三件事：

### 5.1 callback identity

runtime 需要一个能把 callback target 和普通 grain 区分开的身份格式。

它至少要表达：

- 这是 callback，不是 grain
- 它属于哪个 node
- 它自己的局部 id 是什么

### 5.2 local callback registry

source node 需要一个本地注册表，负责：

- 注册 callback target
- 按 id 找到真实对象
- 显式注销
- 在 host 关闭时一起清理

### 5.3 activation execution context

grain 在执行 callback 时，必须能拿到“当前 activation 对应的 runtime”。

不然它没法在正确的 node 身份下把 callback 送出去。

## 6. 为什么 callback 也应该走 response channel

这点很关键。

即便 callback target 只是个本地注册对象，它也不该变成：

`message 到了本地 node 以后，直接 fire-and-forget`

因为 callback 本身也是一次 invocation，它同样可能出现：

- 执行失败
- 目标已经注销
- response 迟到
- source 等待结束

所以 callback 也应该复用现有 response channel，而不是另起一套“特殊旁路”。

这样 grain 发 callback 时，看到的语义和普通 grain 调用是一致的：

- 成功就返回
- 失败就抛错
- 对端不存在就返回 invocation failure

## 7. 当前这版怎么落

当前这版 `OrleansReplicaKernel` 里，这条线先收成了几层：

1. `CallbackTargetIdentity`
   - 给 callback target 单独定义 id 形状
2. `LocalCallbackDirectory`
   - 只管本地 callback target 的注册、查询、注销、清理
3. `LocalGrainRouter`
   - 看到 callback id 时，不再查 grain directory，而是直接路由回 callback 所属 node
4. `ActivationExecutionContext`
   - 在 activation turn 内暴露当前 runtime
5. grain 方法里如果要发 callback
   - 不直接拿 source 创建时的 proxy 去调用
   - 而是用当前 activation runtime 按 callback handle 再发一次 invocation

这等于是先把 callback target 做成一条独立的 local target line，再用现有消息系统把它连回去。

## 8. 这一版最值得跑的两条验证

### 8.1 remote grain -> source callback target

第一条最重要：

1. source node 注册一个 observer callback target
2. 把 grain owner 挪到远端 node
3. 调 grain
4. grain 在远端执行
5. grain 再 callback 回 source node 的本地对象

如果这条通了，说明 callback 没有偷懒走同进程直调，而是真走了 node 间 invocation。

### 8.2 callback target disposed

第二条同样重要：

1. source node 注册 callback target
2. grain 拿到 handle
3. source node 显式注销 callback target
4. grain 再拿旧 handle 去调
5. runtime 必须明确失败

这条验证的是 callback target 的生命周期边界：

`它不是 grain，不存在就该失败，不该悄悄变成 no-op`

## 9. 当前版本故意还没做什么

这一版还没有做：

1. 完全透明的 object reference API
2. callback target 的真正序列化表示
3. observer 生命周期和 client/gateway 的正式接线
4. callback target checkpoint / recovery
5. callback target 的背压、批量发送、顺序保证
6. callback target 的独立 response retention policy
7. extension / observer / stream callback 的统一抽象

所以这一篇补的是：

`让 grain 可以通过 runtime 正经地回调一个 source node 本地注册对象`

还不是最终版 Orleans object reference 全家桶。

## 10. 为什么完整复刻 Orleans 迟早要补这层

只把普通 grain 调用做完，系统还不算真的长成 Orleans。

因为 Orleans 很多能力天然都依赖“反向目标”：

- observer
- callback object
- 某些 client-side target
- 后面更重的扩展点

这些能力如果 runtime 里没有单独的 callback target 表达，最后就只剩两条路：

1. 全部偷懒做成内存直调
2. 全部硬塞成普通 grain

前者会直接绕开消息语义，后者会把模型弄脏。

所以 callback target 这一层虽然看起来不如 placement、membership 那么“核心”，但它其实是 runtime 从“只会单向调 grain”走向“能承载 Orleans 那套交互模型”的关键一步。
