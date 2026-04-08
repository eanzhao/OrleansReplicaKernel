# 失败路径、超时与重试：Orleans 的容错到底该怎么分层（第四十三篇）

这一篇承接前面的 [41-distributed-directory-and-owner-model.md](./41-distributed-directory-and-owner-model.md) 和 [42-remote-forwarding-and-message-loop.md](./42-remote-forwarding-and-message-loop.md)。

41 讲的是 `directory / owner / locator / invalidation` 怎么切。42 讲的是 `directory` 解决“去哪儿”，`message loop` 解决“怎么送过去再带回来”。

但如果只做到这里，失败链还是没讲透。因为真正麻烦的不是“调用能不能走通”，而是“调用失败时到底是哪一层坏了，坏了以后该谁收口”。

所以这一篇只盯住一个问题：

> 超时、远端失败、目录过期、重试，这几步到底该怎么分层。

如果这件事不先讲清楚，后面系统一旦遇到失败，就会开始乱套：

- routing 想顺手做重试
- transport 想顺手改目录
- directory 想顺手补回包
- timeout 想顺手触发重定位

最后每一层都在管别人的事，代码就会再次变脏。

---

## 1. 先说结论

如果我要重建一版 Orleans，我会把失败链说死成三类问题：

- `消息没送到`
- `远端执行失败`
- `目录信息过期`

这三类问题看上去都叫“失败”，但它们不在同一层。

### 1.1 消息没送到

这类问题属于 `transport / message loop` 层。

它的典型表现是：

- 远端节点不可达
- 连接断了
- 请求在路上丢了
- 回包迟迟没回来

这类问题的本质不是 grain 执行失败，而是消息回路没完成。

### 1.2 远端执行失败

这类问题属于 `remote runtime / activation / grain` 层。

消息已经送到了，远端也收到了，但在执行过程中：

- grain 抛异常
- activation 失败
- 远端 runtime 报错

这类失败要通过 response path 原样带回来，而不是伪装成 transport 失败。

### 1.3 目录信息过期

这类问题属于 `directory / locator` 层。

它的意思不是“调用没送到”，也不是“grain 执行失败”，而是：

- owner 变了
- 节点已经不是 owner
- 缓存还拿着旧地址
- 目录记录不可信了

这类问题要通过 invalidation 和重新定位处理，不能直接当成运行时异常。

---

## 2. 超时属于哪一层

这个问题很关键，因为超时是最容易被写歪的。

### 2.1 超时首先属于 message loop

超时是请求/响应回路的管理问题，不是 routing 的职责。

更准确地说，超时关心的是：

- 一个 `RequestId` 对应的 pending call 等了多久
- 回包有没有在允许窗口内回来
- 等待点要不要被收口

所以超时应该挂在 `message loop / pending call registry` 上，而不是挂在 directory 上。

### 2.2 超时不等于远端失败

这两件事不能混。

- 超时表示“我这边没等到结果”
- 远端失败表示“对方执行时出了问题”

超时有可能只是 transport 慢了，远端其实后来还活着。
远端失败则是执行链已经明确报错。

如果把超时直接等同于远端失败，后面就会误伤目录和重试逻辑。

### 2.3 超时也不该直接塞进 routing

routing 的职责是决定下一跳，不是决定一个 request 该等多久。

如果 routing 顺手做 timeout，它就会开始关心：

- pending call
- 时间窗口
- response 收口

这会把决策层和等待层糊在一起。

正确的做法是：

- routing 决定去哪里
- message loop 决定等多久
- timeout 到了以后，message loop 再通知上层失败

---

## 3. 失败回包和 transport 失败有什么差别

这两者在 runtime 里必须分开，否则失败信号会乱。

### 3.1 失败回包是“远端已经收到了，但执行出错”

失败回包说明消息回路是通的：

- 请求送到了
- 远端执行了
- 结果以异常形式回来了

这类失败应该通过 `InvocationResponseMessage.Error` 这种路径回传。

它的价值是：调用方知道远端真的执行过了，只是执行失败。

### 3.2 transport 失败是“消息没送到，或者回不来”

transport 失败说明问题还在消息回路里，不在 grain 执行里。

典型情况有：

- 目标节点不存在
- 传输入口不可用
- 回包没法送回

这类失败不能包装成普通 grain 异常，因为它不是业务执行异常。

### 3.3 两者的恢复策略也不同

失败回包通常意味着：

- 远端执行路径有问题
- 结果已经可知
- 可以决定是否重试或直接上抛

transport 失败通常意味着：

- 先怀疑目录记录是否还有效
- 先怀疑 owner 是否变化了
- 先做 invalidation

所以“执行失败”和“消息没送到”不能走同一条恢复分支。

---

## 4. directory invalidation 在失败链里干什么

directory invalidation 不是失败处理的全部，它只是失败链里负责“把旧地址判死”的那一段。

### 4.1 invalidation 处理的是目录记录，不是调用结果

如果远端失败了，或者 transport 失败了，首先坏掉的通常是：

- 旧 owner 记录
- locator cache
- 路由的地址判断

而不是本地 activation 本身。

所以 invalidation 的任务是：

- 让旧定位结果不再可信
- 让后续调用重新查 directory
- 不再盲信旧地址

### 4.2 invalidation 不是 retry

这点要说得很死。

invalidation 只负责把旧地址作废。
retry 负责决定“要不要再发一次”。

如果把两者绑死，后面就会出现这种坏味道：

- routing 一边查目录一边重试
- transport 一边失败一边改 owner
- directory 一边失效一边调度下一次调用

这会让系统完全失去边界。

### 4.3 invalidation 的目标是让后续调用变得正确

它不是为了把当前请求救活，而是为了避免下一次继续走错。

这就是为什么它应该挂在目录语义上，而不是挂在执行语义上。

---

## 5. 为什么重试不该直接塞进 routing

这也是很容易写歪的地方。

### 5.1 routing 负责“这次该去哪儿”

routing 的输入应该是：

- 请求
- owner 语义
- 当前 cache 是否可信

routing 的输出应该是：

- 本地执行
- 远端转发
- 目录失效后重新定位

它不该顺手决定“失败后要重试几次”。

### 5.2 重试是调用策略，不是定位策略

retry 属于调用策略层，和定位策略不是一回事。

比如：

- 远端失败要不要换 owner 再试一次
- 超时以后要不要重新查目录
- transport 失败后要不要直接上抛

这些都是调用层的判断，不是 routing 层的判断。

### 5.3 把 retry 塞进 routing，会把失败和定位混成一个开关

一旦这么做，routing 就会开始关心：

- 重试次数
- 回退策略
- 超时处理
- pending call

最后 routing 会变成一个“小型容错框架”，而不是一个清楚的定位层。

所以重试可以存在，但应该挂在：

- message loop
- invocation policy
- caller side retry policy

而不是挂在 routing 本体上。

---

## 6. 最小可接受的失败处理闭环

如果只做最小闭环，我会把失败处理写成 5 步。

### 6.1 第一步：请求发出去

message loop 生成 `RequestId`，并记录 pending call。

### 6.2 第二步：根据 routing 决定去向

如果 owner 是本地，就本地执行。

如果 owner 是远端，就通过 transport 投过去。

### 6.3 第三步：根据结果分类

结果只分三类：

- 成功回包
- 远端执行失败回包
- transport / timeout 失败

### 6.4 第四步：必要时做 invalidation

如果失败是因为 owner 不可信、地址过期、节点不可达，就先把目录记录和 locator cache 打掉。

### 6.5 第五步：决定是否重试

重试不是默认动作，而是上层策略。

有些失败应该立刻重试，有些应该直接上抛。

但不管重不重试，目录失效和失败分类都得先做对。

这就是最小闭环：

`send -> wait -> classify -> invalidate if needed -> retry or fail`

而不是：

`send -> 出错 -> routing 里再绕一圈`

---

## 7. 如果重建一版，该怎么切这些抽象

如果从头重建，我会把失败链切成 6 层。

### 7.1 Invocation policy

这一层负责：

- 发起调用
- 管理 pending call
- 处理超时
- 决定是否重试

### 7.2 Routing

这一层只负责：

- 根据 owner / cache 决定路径
- 判断本地还是远端
- 触发重新定位的入口

### 7.3 Transport

这一层只负责：

- 发送请求
- 接收响应
- 处理节点不可达

### 7.4 Remote runtime

这一层只负责：

- 收到请求
- 找到 activation
- 执行 grain
- 返回 response

### 7.5 Directory

这一层只负责：

- owner 记录
- cache 失效
- 重新定位

### 7.6 Error classification

这一层只负责：

- 把失败分成超时、执行失败、消息没送到、目录过期
- 决定哪些失败要触发 invalidation
- 决定哪些失败值得 retry

这六层分开以后，系统才会真的可推理。

---

## 8. 这篇真正要守住的边界

如果只记住一句话，我会记这一句：

> 超时和重试属于调用策略，失败回包属于远端执行语义，directory invalidation 属于定位语义。

这三件事可以串起来，但绝不能合成一层。

如果把它们混在一起，系统表面上看似更“省事”，实际会很快回到那种：

- routing 在管失败
- transport 在管目录
- directory 在管超时

的脏状态。

Orleans 历史上很多复杂度，最开始都不是因为某个功能太难，而是因为失败语义没有先分层。

