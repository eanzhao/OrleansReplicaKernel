# OrleansReplicaKernel

`OrleansReplicaKernel` 的完整目标，是在新的架构边界和实现组织下，完整复刻 Orleans。

当前这份代码还远远没有做到“完整复刻”，但已经把一批最核心的 runtime 主链先跑起来了。现在的状态更准确地说，是“面向完整复刻目标的早期内核阶段”，而不是一个故意只做到一小半的 toy。

## 已做到

- `GetGrain -> grain reference -> IInvokable -> message -> routing -> activation -> scheduler -> response` 这条主调用链已经跑通。
- 单 activation 串行调度已经有了，基础的 turn 执行模型已经立住。
- grain identity、address、invocation、message、routing、runtime 这些核心分层已经拆开。
- 单进程内的多节点模拟已经有了，远端转发和响应回包也已经打通。
- grain directory、locator、owner 迁移、缓存失效这条链已经有最小实现。
- callback target / observer 这条反向调用链已经有第一版最小实现，远端 grain 已经能通过 runtime 回调 source node 本地注册对象。
- observer reference 已经有第一版透明 API，grain 端现在可以直接拿强类型 observer reference 发回调，而不是手工传裸 handle。
- object reference 的第一版“表示 -> 目标端 rehydrate”边界已经补上，observer reference 不再依赖对象本体直接过 invocation 边界。
- object reference factory registry 已经接进 builder / host / runtime，typed object reference 的创建和目标端重建现在共用一份注册表。
- generated object reference metadata + builder 级 assembly scan 已经接上，当前这批 generated object reference 不再需要在应用入口逐个手工 `AddObjectReference<T>()`。
- membership 的早期主链已经有了：
  `probe -> failure detector -> authoritative membership -> gossip dissemination -> local membership view -> stabilization`
- partial fanout 和 anti-entropy 这两类 dissemination 行为已经有最小实现。
- runtime checkpoint 已经能导出并恢复 membership、directory owner records、activation metadata 等运行时元数据。
- grain directory checkpoint 和 activation metadata recovery 已经有最小恢复闭环。
- initial placement、load-aware rebalancing、owner handoff 已经有最小实现。
- warm handoff、capture/apply fallback、quiescence/drain 这些 handoff 关键边界已经做了第一版。
- idle collection 已经能回收 activation，并验证 metadata 不会被误当成活实例。
- `docs/` 下面已经整理出一套从 Orleans 源码分析到复刻路线的文档主线。

## 仓库结构

- `docs`：对 Orleans 主仓库的源码研究文档
- `src/OrleansReplicaKernel`：当前这版最小运行时原型

## 代码目录

- `App`：宿主装配和演示入口
- `Identity`：`GrainId` / `GrainAddress`
- `Invocation`：强类型引用和 `IInvokable`
- `Messaging`：请求/响应消息
- `Scheduling`：单 activation 串行调度
- `Routing`：grain directory / locator / router / placement / rebalancing / relocation / local activation directory
- `Runtime`：runtime、transport、membership、probe、gossip、local membership view、failure detector
- `Demo`：示例 grain 和模拟生成代码

## 还没做到

- 真正的跨进程、跨机器网络传输还没做，现在还是同进程多节点模拟。
- 真正可落地的 membership storage、cluster membership backend、持久化视图传播还没做。
- 真正的 distributed grain directory、多副本目录、一致性协议、目录 owner 协调还没做完整。
- 真正的 placement 策略体系、跨节点负载统计、正式的 rebalancing/handoff 协议还没做完整。
- 真正的 state storage provider、persistent state、事务、streaming、reminder、timer、provider 生态都还没进入实现阶段。
- 真正的 client、gateway、序列化运行时、代码生成器、application part、provider 装配体系还没接到当前内核里。
- 真正完整的 object reference / observer 序列化协议、跨进程 rehydration、callback 与 client/gateway 的正式接线还没做完。
- 真正完整的 application part / metadata manifest / 多程序集自动发现体系还没接进来；现在的 object reference 自动发现还只是先补到 builder + assembly scan 这一层。
- 真正的故障恢复、节点重启恢复、rolling upgrade、兼容性和版本演进都还没有正式实现。
- 真正的可观测性、诊断、管理面、系统 target、测试基建和性能基准还没有迁进这个新仓库。
- 真正的线上级别 fencing、stale message rejection、rollback orchestration、handoff state serialization/transport 还没做完整。
- 真正意义上的“完整复刻 Orleans”还没有完成，现在只是先把最核心、最容易决定架构形状的内核运行时主链搭起来了。

## 当前实现主线

### 调用主链

1. `GetGrain`
2. 强类型引用把方法调用打包成 `IInvokable`
3. runtime 生成 `InvocationMessage`
4. router 找到 owner
5. 如果 owner 在本地，就走本地 activation directory
6. 如果 owner 在远端，就走同进程 transport
7. activation scheduler 串行执行 turn
8. 返回结果

### Membership / Placement / Directory / Activation Recovery 主链

1. `InProcessClusterProbeService` 对 peer 做 probe tick
2. `ConsecutiveFailureDetector` 把 probe 结果折叠成 authoritative membership 上的状态变更
3. `InProcessClusterMembership` 负责维护 authoritative view、epoch 和 `MembershipViewChange`
4. `InProcessMembershipGossiper` 负责 dissemination：
   - 正常 tick：只挑一部分 observer 做 fanout
   - anti-entropy tick：专门给 lagging observer 补齐漏掉的变化
5. `GossipedClusterMembershipView` 不再自己去拉 authoritative membership，而是只消费 gossiper 发来的变化
6. `GossipedClusterMembershipView` 内部再跑 stabilization，把 observed 状态延迟提升成 stable 状态
7. `OrleansReplicaKernelHost.CaptureRuntimeCheckpoint()` 会把 authoritative membership、observer view、gossiper dissemination cursor、grain directory owner 记录、activation metadata 一起打包成恢复点
8. builder 可以用 runtime checkpoint 重建 membership、directory 和 activation metadata 层，这样 restart 后不必重新走初始化传播
9. `InMemoryGrainDirectory` 在第一次看到 grain 时，会通过 `LeastLoadedPlacementPolicy` 按健康状态和 activation load 选初始 owner
10. `LoadSkewRebalancingPolicy` 只负责输出“要不要 handoff 到更空的节点”
11. `OrleansReplicaKernelHost` 在 handoff 前会尝试从旧 activation 捕获一份 `ActivationHandoffRecord`，再把它暂存到目标 node
12. `ActivationEntry` 在 handoff 前会先进入 quiescing，拒绝新 turn，并等旧 turn drain 完
13. `LocalActivationDirectory` 只在目标 grain 下次真正创建 activation 时，才会把这份 warm handoff state 应用进去
14. 如果 `capture / stage / apply` 失败，系统会记日志并退回 cold handoff / cold activation，不做全局 rollback
15. `LocalActivationDirectory` 恢复的是 activation metadata，不是 activation instance；真正的 grain 实例还是在下一次 `GetOrCreate` 时重新创建
16. `InMemoryGrainDirectory` 和 `HealthyNodeRelocationPolicy` 只看 stable 状态，所以 relocation 会慢于 authoritative membership

这个设计里故意保留了两层“慢半拍”：

- 第一层是 dissemination：因为 fanout 不是全量广播，所以不是每个 observer 都会在同一个 tick 收到变化
- 第二层是 stabilization：即便 observer 已经收到 `Unhealthy`，也不会立刻把 stable 状态翻过去

这两层叠在一起，才更接近真实系统里 membership dissemination 的味道。

checkpoint 在当前实现里扮演的角色也刻意收得很窄：

- 它是 restart recovery 的加速器
- 它不是新的 authoritative truth
- 真正的集群事实，仍然来自 authoritative membership 加后续传播和校准
- activation metadata 可以加速恢复，但不会替你恢复真实实例内存
- warm handoff state 只服务在线 owner 切换，也不会被 checkpoint 直接持久化下来
- warm handoff fallback 也只服务在线迁移，不会把失败补偿扩散成 checkpoint rollback
- quiescence/drain 只服务 handoff 顺序边界，不负责持久化和目录事实

## Program 演示什么

`Program.cs` 现在会这样跑：

1. 起 3 个 node：`dev-node-1`、`dev-node-2`、`dev-node-3`
2. gossip 参数设成：
   - `fanout = 1`
   - `antiEntropyInterval = 4`
3. 先让 `IEchoGrain` 本地跑两次，再把 owner 挪到 `dev-node-2`，确认 warm handoff state 能跟着一起过去
4. 连续制造两次对 `dev-node-2` 的 probe miss
5. 每次 probe 之后都只跑一轮 fanout gossip，并打印这轮实际发给了哪个 observer
6. 这时你会看到：
   - authoritative membership 已经变了
   - 但不是所有 observer 都已经收到
   - 收到的 observer 里，也不是立刻 stable `Unhealthy`
7. 再等 stabilization window 过去，跑下一轮 gossip；因为这个 demo 设的是 `antiEntropyInterval = 4`，所以这一轮会切到 anti-entropy
8. anti-entropy 会把 lagging observer 追平，同时让已经收到变化的 observer 完成 stabilization
9. 接着把 `counter` 先跑热，再导出一份 runtime checkpoint，里面会保存 authoritative epoch、observer 本地 view、dissemination cursor、directory owner 记录、activation metadata
10. 销毁 host，再用 runtime checkpoint 重建一份新 host
11. 重建后的第一眼 membership view、grain directory 和 activation metadata 都已经是恢复过的，不需要再从空状态开始同步
12. 这时直接调 echo，directory 会用恢复出来的 owner 记录配合 stable local view 立刻做 relocation，不需要重新从空目录学习
13. 再新建一个 `fresh-placement` grain，确认 initial placement 会跳过不健康节点，优先放到更空的 `dev-node-3`
14. 然后对 `echo/alpha` 跑一次 `RebalanceGrainAsync`，确认 rebalancing 只做决策，handoff 才真的切 owner；而且因为 `EchoGrain` 支持 warm handoff，计数会跟着一起迁过去
15. 再单独跑一条 `echo/fallback`，分别注入一次 apply 失败和 capture 失败，确认系统都会自动退回 cold path
16. 再跑一条 `echo/drain`，先发一个慢调用，再在它还没结束时 handoff，确认旧 turn 会先 drain 完，再切 owner
17. 再调 `counter` 时，你会看到先命中“恢复出来的 activation metadata”，但真实实例仍然是新建的，所以计数不会延续到 checkpoint 前的值
18. 最后再用 idle collection 验证 activation metadata 也不会被错误地当成活实例

## 运行

```bash
dotnet run --project src/OrleansReplicaKernel/OrleansReplicaKernel.csproj
```

如果你想直接用这套独立 solution，就在仓库根目录跑：

```bash
dotnet build OrleansReplicaKernel.slnx
dotnet run --project src/OrleansReplicaKernel/OrleansReplicaKernel.csproj
```

输出里重点看这几类日志：

- `probe`：哪轮 probe 命中了、哪轮 miss 了
- `membership`：authoritative membership 上产生了哪些变化
- `gossip`：每轮 dissemination 实际送给了哪个 observer、送了哪些 epoch
- `result ... deliveries`：更适合直接看 demo 的 fanout / anti-entropy 结果
- `stabilization`：某个 observer 什么时候把 observed 状态提升成 stable 状态
- `runtime-checkpoint` / `membership-after-restart`：runtime checkpoint 导出和 restart recovery 的效果
- `directory-after-restart` / `activation-metadata-after-restart`：directory owner 记录和 activation metadata 的恢复效果
- `placement`：第一次看到新 grain 时，initial placement 选中了哪个健康节点
- `rebalancing`：负载不均时，policy 认为应该把 grain 迁到哪儿
- `handoff-state`：旧 activation 有没有导出 warm handoff state，目标 activation 有没有接到
- `handoff`：真正执行 owner 切换、locator 失效和旧 activation 下线的动作
- `fallback to cold handoff` / `fallback to cold activation`：warm handoff 失败时系统是在哪一层退回保底路径的
- `begin quiesce` / `drained quiescing activation`：旧 activation 是不是先安静下来，再进入 handoff
- `placement-load-after-*`：每轮演示之后三个节点各自的 activation load
- `relocation`：owner 什么时候真正被迁走
- `retry`：runtime 什么时候先命中旧地址，再做 invalidation + retry

## 当前还没覆盖的具体能力

- 真网络
- 真 membership storage
- 真 gossip fanout 策略优化
- 真 anti-entropy session / digest
- 真 checkpoint persistence / reload IO
- 真多副本目录
- 真分布式 placement / handoff 协议
- 真 handoff state serialization / transport
- 真 handoff rollback orchestration
- 真 quiescing request forwarding / fencing
- 真 probe timeout / ping payload

当前版本主要是在把 authoritative membership、gossip dissemination、local membership view、runtime checkpoint、grain directory、activation metadata、initial placement、load-aware rebalancing、warm handoff、handoff fallback、quiescence/drain 这些层先拆开，并让它们先形成一条可运行、可验证、可继续扩展的内核主线。
