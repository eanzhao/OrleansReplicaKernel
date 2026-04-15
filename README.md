# OrleansReplicaKernel

在新的架构边界和实现组织下，完整复刻 Orleans 分布式 actor 运行时。

当前处于早期内核阶段：最核心的 runtime 主链已经跑通，但离完整复刻还有明显距离。

## 仓库结构

```
├── docs/                              # Orleans 源码分析与复刻路线文档（71 篇）
├── src/OrleansReplicaKernel/
│   ├── App/                           # 宿主装配与生命周期
│   │   ├── OrleansReplicaKernelBuilder    # fluent 构建器
│   │   ├── OrleansReplicaKernelHost       # 运行时门面 API
│   │   ├── CallbackLease                  # 回调目标生命周期
│   │   └── TraceLog                       # 诊断日志
│   ├── Identity/                      # GrainId / GrainAddress / CallbackTargetIdentity
│   ├── Invocation/                    # IInvokable / 强类型引用 / object reference 序列化
│   ├── Messaging/                     # InvocationMessage / InvocationResponseMessage
│   ├── Scheduling/                    # 单 activation turn 调度与方法级 interleaving
│   ├── Routing/                       # 目录 / 定位 / 路由 / 放置 / 再均衡 / 本地 activation 管理
│   ├── Runtime/                       # 调用运行时 / 传输 / membership / 探测 / gossip / 故障检测
│   ├── Demo/                          # 示例 grain 与模拟生成代码
│   └── Program.cs                     # 端到端演示入口
└── test/OrleansReplicaKernel.Tests/   # 单元测试（46 个）
```

## 架构主线

### 调用链

```
GetGrain<T>(key)
  → grain reference 把方法打包成 IInvokable
    → runtime 生成 InvocationMessage
      → router 查目录找 owner
        → 本地：activation directory → scheduler → turn 执行
        → 远端：transport 转发 → 目标 node 接收 → 同上
          → response 回传 → 去重 / 分类 → 返回结果
```

### Membership 链

```
probe tick → failure detector → authoritative membership (epoch)
  → gossip dissemination (fanout + anti-entropy)
    → local membership view → stabilization window → stable status
```

### Placement / Directory / Handoff 链

```
首次访问 grain → LeastLoadedPlacementPolicy 选 owner
  → InMemoryGrainDirectory 记录 owner + version

owner 迁移 → quiesce 旧 activation → capture warm handoff state
  → directory 更新 owner version → fence 旧/新节点
    → 目标 node stage handoff state → 下次 GetOrCreate 时 apply
      → capture/apply 失败时退回 cold handoff
```

## 已实现能力

| 领域 | 能力 |
|------|------|
| **调用** | 完整主链：GetGrain → reference → invokable → message → routing → activation → scheduler → response |
| **调度** | 单 activation 串行 turn；方法级 interleaving hint；request-chain reentrancy |
| **目录** | grain directory + locator + owner 迁移 + cache 失效；versioned fencing |
| **放置** | least-loaded initial placement；prefer-local hint；load-skew rebalancing |
| **传输** | 同进程多节点模拟；延迟/丢包/重放/重复注入 |
| **Membership** | probe → failure detector → authoritative view → gossip (fanout + anti-entropy) → stabilization |
| **Handoff** | warm handoff capture/apply；quiescence/drain；capture/apply 失败自动退回 cold path |
| **回调** | callback target / observer；跨节点回调；object reference rehydrate；request-chain 跨节点重入 |
| **Timer** | activation-owned timer；不越过独占 turn；随 activation 一起取消 |
| **元数据** | generated attribute + assembly scan 自动发现 grain implementation / reference / object reference |
| **恢复** | runtime checkpoint 导出/恢复 membership + directory + activation metadata |
| **回收** | per-grain-type idle collection age |

## 未实现能力

- 跨进程/跨机器网络传输
- membership storage backend / 持久化视图传播
- distributed grain directory（多副本、一致性协议）
- state storage provider / persistent state / 事务
- streaming / pub-sub / queue adapter
- reminder / system target / 完整 timer 生态
- client / gateway / 序列化运行时 / 代码生成器
- 完整的 application part / metadata manifest
- 完整的 reentrancy / interleaving 模型
- 完整的 object reference 跨进程 rehydration
- 故障恢复 / rolling upgrade / 版本演进
- 可观测性 / 诊断 / 管理面 / 性能基准

## 设计要点

**两层"慢半拍"**：dissemination 不是全量广播（fanout 只挑部分 observer），stabilization 延迟提升 stable 状态。叠在一起更接近真实系统的 membership dissemination。

**checkpoint 角色收窄**：checkpoint 是 restart recovery 的加速器，不是 authoritative truth。集群事实来自 authoritative membership + 后续传播校准。activation metadata 加速恢复但不恢复实例内存。

**handoff fallback**：warm handoff 的 capture / stage / apply 每一步失败都会记日志并退回 cold path，不做全局 rollback。

## 运行

```bash
dotnet run --project src/OrleansReplicaKernel/OrleansReplicaKernel.csproj
```

运行测试：

```bash
dotnet test
```

### 关键日志标签

| 标签 | 含义 |
|------|------|
| `probe` | probe 命中 / miss |
| `membership` | authoritative membership 状态变更 |
| `gossip` | dissemination 送给了哪个 observer、哪些 epoch |
| `stabilization` | observer 把 observed 状态提升成 stable |
| `placement` | initial placement 选中了哪个节点 |
| `scheduler` | turn 是 exclusive 还是 interleavable |
| `handoff` / `handoff-state` | owner 切换 / warm handoff state capture/apply |
| `fencing` | stale message rejection |
| `retry` | runtime invalidation + retry |
| `timer` | activation timer 注册 / 触发 / 取消 |

## Program 演示场景

Demo 在 3 个模拟节点上依次验证：

1. **基本调用** — 本地 echo + 远端 owner 迁移
2. **Fencing** — stale address rejection + invalidation retry
3. **去重** — 丢包后重试命中 deduplication cache
4. **超时** — late response 到达后被分类丢弃
5. **响应排序** — stale/duplicate response 分类
6. **回调** — remote grain 回调 source node observer + dispose 后调用失败
7. **重入回调** — observer callback 跨节点重入原 grain
8. **Timer** — 独占 turn 内注册 timer 不插队；deactivate 取消 timer
9. **Membership** — 连续 probe miss → gossip fanout → anti-entropy → stabilization
10. **Checkpoint** — 导出 runtime checkpoint → 销毁 → 重建 → 恢复后调用
11. **Placement** — least-loaded 放置 + prefer-local hint
12. **Interleaving** — 标记方法并发执行，耗时 < 串行
13. **Reentrancy** — 同 grain 自调用不死锁
14. **Warm handoff** — rebalance 带 state 迁移 + apply/capture 失败退回 cold path
15. **Drain** — 慢调用期间 handoff，旧 turn drain 后再切 owner
16. **Idle collection** — per-grain-type collection age 差异化回收
