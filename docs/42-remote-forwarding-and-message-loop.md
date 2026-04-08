# 远端转发与消息回路：为什么 owner 目录还不够（第四十二篇）

这一篇承接前面的 [40-single-node-to-multi-node-plan.md](./40-single-node-to-multi-node-plan.md) 和 [41-distributed-directory-and-owner-model.md](./41-distributed-directory-and-owner-model.md)。

41 讲的是目录、owner、locator、invalidations 这些东西怎么分层。可如果只做到这里，多节点还是跑不起来。因为目录只能回答“该去哪儿”，不能回答“怎么把调用送过去，再把结果带回来”。

所以这一篇只盯住另一半：

> 目录负责决定去哪儿，消息回路负责把调用送过去并把响应带回来。

这两个层次如果混了，最后就会把 routing、transport、回包、超时、失效全揉成一锅。Orleans 的调用链之所以看着短，背后其实是这两层被压得很紧，但职责并没有混掉。

---

## 1. 先说结论

如果我要重建一版 Orleans，我会把“远端转发”拆成两段：

- `directory / locator / routing` 负责找目标
- `message loop` 负责完成一次请求/响应的闭环

这里的 `message loop` 不是泛泛的 RPC 术语，而是一个很具体的运行时回路：

- 生成请求消息
- 记录 pending call
- 发送到本地或远端
- 等待回包
- 根据回包完成 task
- 根据失败更新目录状态

如果只做 directory，不做 message loop，就只能知道“该找谁”，却没法让调用真正回来。

如果只做 message loop，不做 directory，就会变成“拿着消息乱发”，调用路径没有 owner 语义，也没有重新定位能力。

---

## 2. 为什么 owner 目录还不够

owner 目录解决的是归属问题，不解决传输问题。

### 2.1 目录只回答“谁负责”

目录里保存的核心信息是：

- 这个 grain 当前的 owner 是谁
- 这个 owner 记录是否还可信
- 旧缓存要不要失效

这足以让 routing 做出判断，但不足以完成调用。

### 2.2 调用还需要“怎么送过去”

一旦 owner 不在本地，接下来至少还要有这些动作：

- 把 `InvocationMessage` 包成可发送的网络消息
- 找到对应的远端 endpoint
- 在远端节点上恢复请求上下文
- 执行 grain
- 把结果或异常带回

这些都不是 directory 的职责。

### 2.3 目录不能吞 transport

如果把 transport 塞进 directory，directory 就会开始关心：

- socket / channel
- 序列化格式
- 重试
- 超时
- 回包

这会把一个原本应该很干净的 owner 语义层，硬生生拖成消息框架。

Orleans 里最值钱的不是“目录里能不能塞更多代码”，而是“目录能不能保持只负责定位语义”。

---

## 3. 消息回路最小需要什么

如果只做最小闭环，我会把消息回路拆成 5 个部件。

### 3.1 Request envelope

请求消息至少要能带这些东西：

- `RequestId`
- `Target`
- `Invokable`
- `RequestContext`

`RequestId` 是这条消息的身份，`Target` 是目录已经决定好的目标，`Invokable` 是真正要执行的调用内容。

### 3.2 Pending call registry

发送出去的调用不能只是“丢出去”，还要能挂回一个等待点。

这个等待点至少要能：

- 通过 `RequestId` 找回对应的 pending call
- 在回包到来时完成 `Task`
- 在超时或失败时把调用收口

没有这个 registry，request 和 response 就只能靠“碰巧对应”，系统会很快失控。

### 3.3 Transport

transport 只负责一件事：

- 把请求消息送到另一端
- 把响应消息送回来

它不该决定 grain 怎么定位，也不该知道 activation 怎么创建。

### 3.4 Remote endpoint

远端节点需要一个接收入口，能把请求消息重新灌回 runtime。

这个入口的职责是：

- 恢复 request id
- 反解目标 grain
- 执行本地 routing / activation / scheduling
- 生成 response

### 3.5 Response path

响应路径要能把：

- 正常结果
- 异常
- 失效信号

带回调用方。

这里的关键不是“回包长什么样”，而是“回包要能精确对应某个 request id”。

---

## 4. 为什么 routing 不能直接吞掉 transport

这点非常重要。

### 4.1 routing 负责决定路径，不负责承载路径

routing 的输入应该是：

- 一个请求
- 一个 owner 语义
- 一个本地/远端判断

routing 的输出应该是：

- 本地 activation
- 远端转发计划

但 routing 不该自己变成传输层。

### 4.2 一旦 routing 吞 transport，就会把三种语义混在一起

这三种语义是：

- 定位语义
- 传输语义
- 执行语义

如果混在一起，代码会变成：

- routing 在改序列化
- transport 在查目录
- activation 在管重试

这就是典型的脏架构。

### 4.3 正确分法是“routing 产出下一跳，transport 执行下一跳”

更自然的切法是：

- routing 决定这次调用是本地还是远端
- transport 把远端调用真正送出去
- remote endpoint 把消息重新放回另一端 runtime

也就是说，routing 是决策层，transport 是执行层。

---

## 5. request id 和回包语义的角色

远端转发一旦出现，`RequestId` 就不是装饰字段了，它是回路的锚点。

### 5.1 request id 解决“这是谁的回包”

同一时刻可能有很多调用在飞：

- 同一个 grain 的多次调用
- 不同 grain 的并发调用
- 本地与远端混合调用

没有 request id，response 回来时就没法知道该唤醒哪个等待点。

### 5.2 request id 也帮助做失败收口

如果某个请求已经超时、被取消，或者目录失效导致需要重新定位，`RequestId` 也能让 pending call 及时清掉。

否则 response 晚到时，很容易把已经失效的调用又重新完成一次。

### 5.3 response 不能只是“结果”

响应不只是一个 value，它至少要表达：

- 成功结果
- 异常
- 目录失效或重定位信号
- 远端不可达

这是因为消息回路不只是 return path，它还承担失败反馈。

---

## 6. 失败后怎么和 directory invalidation 接上

这是 remote forwarding 里最容易写脏的地方。

### 6.1 远端失败不等于 activation 失败

如果远端投递失败，先坏掉的通常不是本地 activation，而是目录记录。

所以失败后第一步应当是：

- 标记 owner 记录过期
- 让 locator 的缓存失效
- 触发重新定位

### 6.2 invalidation 不是重试本身

invalidation 的职责只是让旧地址不再可信。

重试或重新路由是下一层的事：

- 再查 directory
- 再算 owner
- 再决定本地还是远端

如果把 invalidation 和 retry 绑成一个步骤，后面就很难看清到底是定位错了，还是传输错了。

### 6.3 目录失效和消息回路是闭环，不是同一层

更准确地说：

- message loop 负责把失败带回来
- directory 负责把失败转成失效
- routing 负责基于失效重新选择路径

这三个动作要连起来，但不能合成一个概念。

---

## 7. 如果重建一版，该怎么切这些抽象

如果从头重建，我会把这条链切成 6 层。

### 7.1 Directory layer

只管 owner、失效、重新定位。

### 7.2 Routing layer

只管本地还是远端，输出下一跳决策。

### 7.3 Message loop

只管 request id、pending call、response 完成。

### 7.4 Transport layer

只管把消息送过去并送回来。

### 7.5 Activation layer

只管本地 activation 的创建、复用、卸载、回收。

### 7.6 Scheduling layer

只管 activation 内部的 turn 顺序。

这样切完以后，调用链会很清楚：

`GrainId -> directory -> routing -> message loop -> transport -> remote runtime -> activation -> scheduler -> response -> pending call`

这条链里，每一层都只干一件事。

---

## 8. 对照现在的 toy runtime

如果对照现在的 `OrleansReplicaKernel`，它已经把前半段做出来了：

- owner 目录
- locator 缓存
- 本地 routing
- activation directory
- scheduler

但它还没有真正的 remote forwarding，所以远端 owner 现在只能明确失败。

这不是坏事，反而是很好的中间状态。因为它说明我们已经把“目录”和“本地执行”分开了，下一步只需要把 message loop 和 transport 补上，就能开始做真正的双节点闭环。

---

## 9. 最后一句

目录负责告诉你去哪儿，消息回路负责把调用送过去再带回来。

这两层如果分不清，多节点就会变成一团；如果分清了，后面的 remote forwarding、失败恢复、owner 失效和回包语义，才有地方各归其位。
