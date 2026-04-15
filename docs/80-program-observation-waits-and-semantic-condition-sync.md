# 80. Program 观察等待收敛与 Semantic Condition Sync

这一篇接在 `79` 后面，补的是 host provider-aware wait helper 真正落到 demo orchestration 之后带来的边界收敛。

前一轮已经让 host 拥有：

- `DelayAsync(...)`
- `CreateTimeoutSource(...)`
- `GetTimestamp()/GetElapsedTime(...)`
- `WaitForAsync(...)`

但如果 helper 只是存在于 host 上，而 demo orchestration 仍然习惯写：

- 先执行某个动作
- 再固定 `DelayAsync(140ms)` / `DelayAsync(200ms)`
- 祈祷目标状态在这个窗口里已经出现

那么调用侧虽然不再吃 wall clock，却还是留着另一类问题：

- 等待语义依旧是“猜一个观察窗口”
- 而不是“等某个状态真正成立”

所以这一轮补的，不是新的底层时间原语，而是：

`把 Program 里的几处 observation wait，从固定睡眠改成基于语义条件的 provider-aware sync。`

## 1. 当前这轮具体改了什么

### 1.1 late response 观察不再靠固定 200ms 睡眠

之前 late response 演示的写法是：

- 调用方超时
- 再固定等 `200ms`
- 然后假定 late response disposition 已经更新

现在改成了：

- 直接等 `GetResponseDispositionSnapshot("dev-node-1").LateResponses == 1`

也就是说，等待的对象变成了：

- 目标语义已经发生

而不是：

- 先随便睡一段时间，再猜它应该发生了

### 1.2 stale / duplicate response 观察改成显式等 disposition 成立

同样地，response replay 和 duplicate 观察现在也不再靠固定 delay，而是改成显式等待：

- `StaleResponses == 1`
- `DuplicateResponses == 1`

这让 demo orchestration 里“什么时候继续往下走”的判据，第一次开始直接绑定到真实运行时状态，而不是绑定到某个猜出来的睡眠长度。

### 1.3 timer hold 观察改成显式等 snapshot

timer hold 场景之前也是先固定等一小段时间，再去读 snapshot。

现在改成：

- 直接等待 `GetTimerSnapshotAsync()` 返回目标值

这件事的意义很直接：

- 不是“等够了，应该差不多了”
- 而是“等 timer callback 真正完成并被观测到”

## 2. 为什么这一步值得单独推进

### 2.1 provider-aware 并不自动等于语义化等待

把 `Task.Delay(...)` 改成 `host.DelayAsync(...)`，解决的是：

- 时间源统一

但它并没有自动解决：

- 这个等待本身是不是语义正确

如果观察窗口只是一个拍脑袋的 140ms/200ms，那么即使它走了 `TimeProvider`，本质上还是 heuristic wait。

### 2.2 demo orchestration 也应该逐步摆脱“睡眠驱动”

这个项目的目标不是做一个永远停留在原型姿态的小 runtime，而是逐步把完整 Orleans 的边界重新搭起来。

既然 host/demo 已经开始形成显式 orchestration API，那么下一步就不该长期依赖：

- 先 sleep
- 再观察

而应该逐步转向：

- 直接等待目标状态收敛

这更接近未来真正的宿主/管理面编排方式。

## 3. 当前这轮的验证

这一轮新增了一条 `HostWaitForAsync_WithProbeDelay_UsesConfiguredTimeProvider` 测试，专门验证：

1. `WaitForAsync(...)` 在显式 probe delay 下仍然吃同一个 `TimeProvider`
2. probe 不会因为真实时间流逝而提前发生
3. 只有 provider 被推进到 probe 边界后，下一轮观察才会发生

这条测试的价值在于，它把 `WaitForAsync(...)` 从“只验证无 delay 的纯轮询”推进成了“连带 probe interval 也受同一时间源控制”的 helper。

## 4. 这一轮真正推进的是哪条边界

如果把 `78`、`79`、`80` 连起来看，host 侧 orchestration API 已经逐步走完了三层：

1. 提供 provider-aware timing helper
2. 提供 provider-aware wait/poll helper
3. demo 开始真的用语义条件 wait 代替固定 observation sleep

第三层很关键，因为它意味着这套 API 不再只是“有了”，而是开始真正约束 demo 和宿主调用模式。

## 5. 当前阶段还不能说成什么

这里还是要讲清楚。

这轮现在做到的是：

- `Program` 里几处核心 observation wait 已经改成 semantic condition sync
- `WaitForAsync(...)` 的 probe-delay 路径也有了 manual-time 验证

这轮还没有做到的是：

1. 所有 demo / 测试等待都已经完全去掉固定睡眠
2. 所有宿主编排逻辑都已经彻底语义化
3. 更完整的管理面 / 监控面等待模型已经设计完成

所以更准确的说法不是：

- “宿主侧等待已经完全摆脱 sleep”

而是：

- “Program 已经开始把 observation wait 从固定窗口推进成语义条件同步”

## 6. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的表述是：

`OrleansReplicaKernel 现在不只让 host 拥有 provider-aware 的 wait helper，也开始让 demo orchestration 里的状态观察真正等待语义条件成立，而不是继续依赖固定睡眠窗口；但这仍然只是宿主编排收口的阶段性推进。`
