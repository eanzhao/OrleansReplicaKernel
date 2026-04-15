# 79. Host Provider-Aware Wait 与 Condition Polling

这一篇接在 `78` 后面，继续把 host 侧时间 helper 从“单次 delay/timeout/elapsed”推进到“条件等待与轮询”。

前一轮已经让 host 具备了这些 provider-aware helper：

- `DelayAsync(...)`
- `CreateTimeoutSource(...)`
- `GetTimestamp()`
- `GetElapsedTime(...)`

这已经覆盖了很多单点时间操作，但还留着一个明显断层：

- 调用侧一旦要“等某个状态变成真”，还是得自己写轮询循环
- 轮询循环里很容易重新掉回 `DateTimeOffset.UtcNow` + `Task.Delay(...)`

当前代码里最明显的例子就是测试侧的本地 `WaitForAsync(...)` helper。底层 runtime 已经逐步共享统一时间源，但等待条件成立的观察逻辑还在手搓 wall-clock 轮询。

所以这一轮补的是：

`让 host 开始提供 provider-aware 的条件等待 helper，把调用侧 wait/poll 也收进统一时间边界。`

## 1. 当前这轮具体加了什么

`OrleansReplicaKernelHost` 现在新增了：

- `WaitForAsync<T>(...)`

它的语义很简单：

1. 反复执行 probe
2. 用 predicate 判断条件是否满足
3. 用 host 当前 `TimeProvider` 的 elapsed 计算 timeout
4. probe 之间默认走 `Task.Yield()`，也可以显式指定 provider-aware probe delay

这意味着 host 侧现在不只会提供：

- 单次等待
- 单次 timeout
- 单次 elapsed 观测

还开始提供：

- 面向条件收敛的 wait/poll helper

## 2. 为什么这一步值得单独推进

### 2.1 delay/timeout 收口后，下一处最容易散掉的就是轮询

只要代码开始写：

- “等某个 timer snapshot 出现”
- “等某个 late response disposition 刷新出来”
- “等某个后台状态转移完成”

就会自然出现 wait loop。

如果这一步没有明确收进 host/helper 边界，那么调用侧很容易重新写出：

- `DateTimeOffset.UtcNow + timeout`
- `await Task.Delay(10)`

这样统一时间源就会在观察逻辑这里重新裂开。

### 2.2 这补的是 orchestration API，而不是单个 demo 技巧

这轮不是只为了把某个测试 helper 删掉。

更重要的是，host 现在开始形成这样一组连续能力：

- delay
- timeout
- timestamp / elapsed
- wait / poll

这说明 host 侧时间 API 已经从“散落的原语”开始变成“可复用的 orchestration surface”。

## 3. 当前这轮的验证

这一轮新增了一条 manual-time 测试，直接验证 `host.WaitForAsync(...)` 的两条核心路径：

1. 当条件在 provider 推进后变为真时，等待会完成
2. 当条件一直不满足时，provider 推进到 timeout 边界后会抛出 `TimeoutException`

同时，原来测试文件里的本地 `WaitForAsync(...)` 也被切掉，相关 manual-time 测试开始直接用 host 的 provider-aware wait helper。

这件事的价值在于，它把“条件等待”也从测试局部技巧，推进成了 host 层可复用能力。

## 4. 当前推进到了哪一层

如果把 `76` 到 `79` 连起来看，host 侧的统一时间语义已经逐步覆盖了：

1. delay
2. timeout
3. timestamp / elapsed
4. condition wait / polling

第四层很关键，因为它开始让“观察某个异步状态何时成立”这件事，也共享同一套时间边界。

## 5. 当前阶段还不能说成什么

这里也要继续区分清楚。

这轮做到的是：

- host 开始提供 provider-aware 的 condition wait helper
- 相关测试开始直接使用这套 helper

这轮还没有做到的是：

1. 所有测试等待都已经完全改造成 provider-aware
2. 更高层宿主/管理面 API 已经围绕 wait/poll 全部定型
3. 未来 client / gateway / external orchestration 的等待模型已经设计完成

所以更准确的说法不是：

- “整个等待模型已经最终定稿”

而是：

- “host 侧已经开始把条件等待和轮询也收进 provider-aware orchestration API”

## 6. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不只让 host 提供 provider-aware 的 delay、timeout 和 elapsed helper，也开始让条件等待与轮询共享这条时间线；但这仍然只是宿主 orchestration API 的阶段性收口。`
