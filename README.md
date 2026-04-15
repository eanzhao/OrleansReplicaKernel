# OrleansReplicaKernel

> 在新的架构边界下，完整复刻 Microsoft Orleans 分布式 Actor 运行时。

**当前阶段：早期内核原型** — 最核心的调用链、调度、目录、放置、成员管理、Handoff 等已经跑通，但离完整复刻 Orleans 还有明显距离。

---

## 这是什么项目

[Microsoft Orleans](https://github.com/dotnet/orleans) 是一个成熟的分布式 Actor（Virtual Actor）框架。它的编程模型很优雅——你写普通的 C# 接口和类，框架帮你处理远程调用、并发、生命周期、集群管理这些分布式系统的复杂问题。

但 Orleans 的内部实现经过多年演进，积累了不少历史包袱（比如 2700 行的 `ActivationData` 类、庞大的默认服务注册表等）。

**本项目的目标**：吸收 Orleans 已被验证的核心语义（强类型引用、Turn-based 调度、Grain Directory、Placement 策略等），用更清晰的架构边界重新实现，最终完整复刻 Orleans 的能力。

---

## 一分钟理解核心概念

| 概念 | 通俗解释 |
|------|---------|
| **Grain** | 一个虚拟对象。你通过接口调用它的方法，不用关心它在哪台机器上 |
| **Activation** | Grain 的一个活实例。同一个 Grain 可以在不同节点上被激活 |
| **Directory** | 一个电话簿——记录每个 Grain 的 Activation 现在在哪台机器上 |
| **Placement** | 新 Grain 第一次激活时，选哪台机器放它 |
| **Membership** | 集群里哪些机器活着、哪些挂了 |
| **Turn** | 一次方法调用。一个 Activation 同一时间只跑一个 Turn（默认串行） |
| **Reentrancy** | 特殊情况下允许同一个 Activation 并发执行多个调用 |
| **Handoff** | 把一个 Grain 从 A 机器搬到 B 机器，尽量带着状态搬（warm handoff） |

---

## 仓库结构

```
├── src/OrleansReplicaKernel/
│   ├── App/          # 启动装配：构建器、运行时门面、诊断日志
│   ├── Identity/     # 身份系统：GrainId、GrainAddress、回调目标身份
│   ├── Invocation/   # 调用协议：IInvokable、强类型引用、对象引用序列化
│   ├── Messaging/    # 消息模型：请求消息、响应消息
│   ├── Scheduling/   # 调度器：Turn 串行执行、方法级 Interleaving
│   ├── Routing/      # 路由：目录查询、放置决策、负载均衡、本地激活管理
│   ├── Runtime/      # 运行时核心：调用执行、传输层、Membership、故障检测
│   ├── Demo/         # 示例 Grain 和模拟生成代码
│   └── Program.cs    # 端到端演示入口
├── test/             # 单元测试（47 个）
└── docs/             # 完整文档（72 篇，见 docs/README.md）
```

---

## 架构主线

### 一次调用的完整旅程

```
你写代码：grain.Echo("hello")
  ↓
代理把方法调用打包成 IInvokable（编译期生成）
  ↓
运行时生成 InvocationMessage
  ↓
Router 查 Directory：这个 Grain 在哪？
  ↓
  ├─ 本地 → Activation Directory → Scheduler → 执行 Turn
  └─ 远端 → Transport 转发 → 目标节点接收 → 同上
  ↓
Response 回传（去重 / 超时丢弃 / Stale 过滤）→ 返回结果
```

### 集群怎么知道谁活着

```
定时探测（probe）→ 发现故障 → 权威 Membership 更新（epoch）
  → Gossip 传播（fanout + 反熵）→ 本地视图 → 稳定窗口 → 稳定状态
```

### Grain 怎么搬家

```
负载不均 → 选新 Owner → Warm Handoff：
  旧节点停收新请求（quiesce）→ 等正在跑的调用结束（drain）
    → 抓取状态（capture）→ 送到新节点 → 新节点应用状态（apply）
    → 目录更新 → 旧消息被拒绝（fencing）
如果 capture/apply 失败 → 自动退回 Cold Handoff（不带状态）
```

---

## 当前实现了什么

| 领域 | 已实现 |
|------|--------|
| **调用** | 完整主链：GetGrain → Reference → Invokable → Message → Routing → Activation → Scheduler → Response |
| **调度** | 单 Activation 串行 Turn；方法级 Interleaving；Request-Chain Reentrancy |
| **目录** | Grain Directory + Locator + Owner 迁移 + 缓存失效 + 版本化 Fencing |
| **放置** | Least-Loaded 初始放置；Prefer-Local 提示；负载倾斜再均衡 |
| **传输** | 同进程多节点模拟；支持延迟/丢包/重放/重复注入 |
| **Membership** | Probe → 故障检测 → 权威视图 → Gossip（fanout + 反熵）→ 稳定化 |
| **Handoff** | Warm Handoff capture/apply；Quiescence/Drain；失败自动退回 Cold Path |
| **回调** | Observer 模式；跨节点回调；Object Reference Rehydrate |
| **Timer** | Activation 级 Timer；不越过独占 Turn；随 Activation 取消；Activation 本地时间开始统一到 TimeProvider |
| **元数据** | 生成 Attribute + 程序集扫描，自动发现 Grain 实现/引用/对象引用 |
| **恢复** | Runtime Checkpoint 导出/恢复 Membership + Directory + Activation 元数据 |
| **回收** | 按 Grain 类型配置不同的空闲回收时间 |

## 还没实现什么

> 注意：这些都是**计划做但还没做到**的，不是"不打算做"。

- 跨进程/跨机器网络传输
- Membership 存储后端 / 持久化传播
- 分布式 Grain Directory（多副本、一致性协议）
- State Storage Provider / Persistent State / 事务
- Streaming / Pub-Sub / Queue Adapter
- Reminder / System Target / 完整 Timer 生态
- Client / Gateway / 运行时序列化 / 代码生成器
- 完整的 Application Part / Metadata Manifest
- 完整的 Reentrancy / Interleaving 模型
- 完整的 Object Reference 跨进程 Rehydration
- 故障恢复 / Rolling Upgrade / 版本演进
- 可观测性 / 诊断 / 管理面 / 性能基准

---

## 快速开始

### 运行演示

```bash
dotnet run --project src/OrleansReplicaKernel/OrleansReplicaKernel.csproj
```

演示在 3 个模拟节点上依次跑 16 个场景，覆盖基本调用、Fencing、去重、超时、回调、Timer、Membership、Checkpoint、Placement、Interleaving、Reentrancy、Warm Handoff、Drain、Idle Collection。

### 运行测试

```bash
dotnet test
```

### 看日志

关键日志标签：

| 标签 | 看什么 |
|------|--------|
| `probe` | 探测命中/未命中 |
| `membership` | Membership 状态变更 |
| `gossip` | Gossip 传给了谁、带了哪些 epoch |
| `stabilization` | 观察到的状态提升为稳定 |
| `placement` | 新 Grain 放到了哪个节点 |
| `scheduler` | Turn 类型（exclusive / interleavable） |
| `handoff` | Owner 切换 / 状态搬运 |
| `fencing` | 过时消息被拒绝 |
| `timer` | Timer 注册/触发/取消 |

---

## 文档导航

项目有 72 篇文档，分两大类：

1. **Orleans 源码分析**（01-37）：逐模块拆解 Orleans 的实现，理解它为什么这么设计
2. **复刻设计文档**（38-72）：每个子系统在新架构下应该怎么实现

详细的阅读指南和分类索引见 [docs/README.md](docs/README.md)。

---

## 设计理念

### "慢半拍" Membership

Membership 信息不是全量广播，而是通过 fanout（只发给部分节点）+ 反熵（定期对齐）来传播。稳定状态也需要等待窗口才提升。这更接近真实系统的行为。

### Checkpoint 是加速器，不是真相源

Checkpoint 用于重启恢复时加速，但集群的权威真相来自权威 Membership + 后续传播校准。Activation 元数据加速恢复但不恢复运行时内存。

### Handoff 总有退路

Warm Handoff 的每一步（capture / stage / apply）失败都会记日志并退回 Cold Path（不带状态迁移），不需要全局回滚。

---

## 项目定位

这个项目**不是**一个故意做小的 demo 或 toy runtime。

它的完整目标是**在新的架构边界和实现组织下，完整复刻 Orleans**。当前代码是阶段性实现，已经做出了一部分内核主链和验证原型，后续会持续向完整复刻推进。
