# 74. Transport 与 Retry TimeProvider 收敛

这一篇接在 `72` 和 `73` 后面，继续把 runtime 里的时间边界往前收。

前两轮已经把两条很重要的时间线接进了统一的 `TimeProvider`：

- activation-local time
- membership authoritative history

但如果只做到这里，消息链上其实还留着一块很明显的“旧时间岛”：

- transport 注入延迟
- dropped response replay delay
- duplicate response delay
- response delivery failure 之后的 retry backoff
- activation quiescing 之后的 retry backoff

这些东西虽然不是业务时间，却直接影响一次请求“什么时候继续往下走”。

如果它们还继续裸吃 wall clock，runtime 的时间语义仍然是不完整的。

这一篇补的，就是把这条 request/response control-path 上的延迟，也开始统一到注入时间源。

## 1. 为什么这块延迟不能继续留在系统时钟里

这里最容易犯的错是把这些 delay 看成“只是测试注入的小工具”。

不是。

它们至少属于两类很真实的 runtime 行为：

### 1.1 transport 注入层的时序行为

当前 in-process transport 支持这些注入：

- request delay
- drop response
- replay dropped response later
- duplicate response later

这些东西的意义不是“做个 demo 看着热闹”，而是为了验证：

- caller 怎么处理迟到 response
- duplicate/stale classification 是否正确
- retry 路径会不会把请求语义写脏

如果这些注入 delay 还是靠真实时间推进，那整条故障/恢复测试链就还会被 wall clock 绑住。

### 1.2 runtime 自己的 retry backoff

`InProcessRuntime.InvokeAsync(...)` 里有两条保守的 retry delay：

- activation quiescing 之后的小 backoff
- response delivery failure 之后的小 backoff

这两条 delay 虽然都只有 `25ms`，但它们是 runtime 自己在控制请求推进节奏，不是业务代码在睡眠。

所以更准确地说，这些 delay 属于：

`protocol progression time`

既然 response retention、activation time、membership history 都已经开始统一到 provider，这条线继续留在系统时钟里就很别扭。

## 2. 当前这版具体补了什么

这轮改动还是不大，但边界继续往前推了一步。

### 2.1 `InProcessMessageTransport` 开始持有 `TimeProvider`

transport 现在也接收 builder 传下来的时间源。

这意味着：

- request injected delay
- replay dropped response later
- duplicate response later

都不再直接调用裸 `Task.Delay(delay, CancellationToken.None)`，而是改成按 provider 解释。

### 2.2 `InProcessRuntime` 的 retry backoff 也改成吃 provider

当前 runtime 里两条 retry delay：

- activation quiescing retry
- response delivery failure retry

也都改成了：

- `Task.Delay(delay, _timeProvider, cancellationToken)`

所以现在 request chain 在这些“继续等一下再重试”的分叉点上，不会再偷偷掉回系统时钟。

### 2.3 builder 把同一时间源传给 transport

这一步还是老原则：

- 不能只在局部 new 一个 provider-aware 版本
- 必须从 builder 一路把同一时间源穿到底

这样 activation、membership、runtime、transport 这几层才真的是同一套时间语义，而不是四个地方各自局部统一。

## 3. 这一轮最值钱的验证

这轮最值得看的验证不是“代码里把某个 `Task.Delay` 改了”。

真正值钱的是两条受控时间测试：

### 3.1 request injected delay 可以只靠推进 provider

现在可以这样验证：

1. 给远端节点注入一次 `80ms` request delay
2. 发起请求
3. 不推进 provider，请求不会完成
4. 推进 `79ms`，还不会完成
5. 再推进 `1ms`，请求就会继续走完

这说明 transport injected delay 已经不再依赖真实时钟。

### 3.2 response delivery retry backoff 也可以只靠推进 provider

现在还可以这样验证：

1. 让远端执行成功，但把 response 故意 drop 掉
2. caller 进入 retry path
3. runtime 会先等 `25ms` backoff
4. 不推进 provider，请求不会完成
5. 推进到 `24ms`，还不会完成
6. 再推进 `1ms`，retry 才继续发生

这说明 runtime 自己的 backoff 也已经被收进了统一时间源。

## 4. 这轮真正推进了哪条边界

如果把 `58`、`72`、`73`、`74` 连起来看，会发现 runtime 的时间语义已经不只是“谁记录时间戳”这么简单了。

它开始覆盖四类东西：

1. response history retention
2. activation-local lifecycle time
3. membership authoritative history time
4. request/response control-path progression time

第四类非常重要，因为它第一次把：

- injected transport delay
- retry backoff
- replay/duplicate scheduling

这些会直接影响协议推进节奏的东西，也纳入了同一条时间边界。

## 5. 当前故意还没说成“整个 runtime 时间系统完成”

这里也要讲清楚。

这轮当前做到的是：

- transport 注入延迟开始吃 `TimeProvider`
- replay / duplicate response 的延迟开始吃 `TimeProvider`
- runtime retry backoff 开始吃 `TimeProvider`
- 对应路径已经有受控时间测试

这轮还没有做到的是：

1. demo grain 自己业务里的 `Task.Delay` 全部 provider 化
2. 所有测试辅助等待都彻底脱离真实时间
3. 整个 host/demo 演示链都能完全 deterministic replay
4. 更大范围的 cluster monitoring round 全链时间统一

所以更准确的定位不是：

- “runtime 里所有 delay 都收完了”

而是：

- “request/response control path 上那些 runtime 自己负责的 delay，已经开始统一到同一时间源了”

## 6. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的说法是：

`OrleansReplicaKernel 已经把 transport 注入延迟和 runtime retry backoff 开始统一到注入的 TimeProvider 上：request delay、dropped response replay、duplicate response 以及 quiescing / response-delivery retry 都可以用受控时间驱动；但这还不是整个宿主与 demo 链条的最终时间形态。`
