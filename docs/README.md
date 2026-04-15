# 文档导航

本目录包含 77 篇文档，分两个阶段：

- **阶段一（01-37）**：Orleans 源码分析 — 逐模块拆解 Orleans 的设计，理解它为什么这么实现、哪些地方做得好、哪些地方有历史包袱
- **阶段二（38-77）**：复刻设计文档 — 每个子系统在 OrleansReplicaKernel 里应该怎么设计，边界怎么切

---

## 推荐阅读顺序

### 第一步：先看这 3 篇，建立全局观

| 编号 | 文档 | 为什么先看 |
|------|------|-----------|
| 01 | [Orleans 源码总览](01-orleans-source-overview.md) | 全局地图：仓库分几层、主干是什么、哪些是核心哪些是外围 |
| 38 | [复刻设计总纲](38-replica-design-blueprint.md) | 新系统的架构蓝图：8 层设计、最小内核、阶段里程碑 |
| 37 | [总索引与阅读地图](37-master-index-reading-map-and-replica-roadmap.md) | 01-36 篇的主题归类和推荐阅读路线 |

### 第二步：理解调用主链（按顺序读）

这几篇串起来就是"一次 Grain 调用从发起到返回"的完整旅程：

| 编号 | 文档 | 讲什么 |
|------|------|--------|
| 02 | [从 GetGrain 到方法返回](02-getgrain-to-method-return.md) | 一次完整调用的链路追踪 |
| 03 | [代码生成：代理、Invokable、序列化器](03-codegen-proxy-invokable-serializer.md) | 编译期怎么把接口调用变成消息 |
| 04 | [序列化运行时链路](04-serialization-runtime-chain.md) | 运行时怎么把消息序列化/反序列化 |
| 05 | [ActivationData：复杂度中心](05-activationdata-complexity-center.md) | 为什么这个类会变成 2700 行的"万能枢纽" |
| 06 | [调度、重入与交错执行](06-scheduling-reentrancy-interleaving.md) | Turn-based 调度、并发规则 |
| 07 | [目录、放置与路由](07-directory-placement-and-routing.md) | 怎么把消息送到正确的节点 |

### 第三步：理解生命周期和宿主

| 编号 | 文档 | 讲什么 |
|------|------|--------|
| 14 | [Grain 生命周期与回收](14-grain-lifecycle-and-collection.md) | Activation 从创建到销毁 |
| 21 | [宿主启动与部署链](21-host-startup-and-deployment-chain.md) | UseOrleans 怎么把整个运行时拉起来 |
| 29 | [源码树与项目依赖图](29-source-tree-and-project-dependency-map.md) | 仓库目录结构和项目间依赖关系 |

### 第四步：看过渡文档，理解"从分析到设计"的转折

| 编号 | 文档 | 讲什么 |
|------|------|--------|
| 33 | [最小复刻切口与路线图](33-minimal-replica-cut-and-roadmap.md) | 承上启下：分析完了，开始规划怎么重建 |

### 第五步：按兴趣选读

下面的文档按主题分组，可以按需阅读。

---

## 按主题分类

### 一、调用与消息

分析 Orleans 的调用机制：

| 编号 | 文档 | 简介 |
|------|------|------|
| 02 | [从 GetGrain 到方法返回](02-getgrain-to-method-return.md) | 一次完整调用的全链路追踪 |
| 03 | [代码生成：代理、Invokable、序列化器](03-codegen-proxy-invokable-serializer.md) | 编译期代码生成管线 |
| 04 | [序列化运行时链路](04-serialization-runtime-chain.md) | 运行时序列化体系 |
| 11 | [客户端网关与回调](11-client-gateway-and-callbacks.md) | 客户端如何通过 Gateway 接入集群 |

复刻设计：

| 编号 | 文档 | 简介 |
|------|------|------|
| 39 | [第一版运行时实现计划](39-first-runtime-implementation-plan.md) | 单机骨架的模块顺序和验收标准 |
| 42 | [远程转发与消息循环](42-remote-forwarding-and-message-loop.md) | 目录决定"去哪"，消息循环负责"怎么送" |
| 55 | [请求去重与过期响应处理](55-request-deduplication-and-stale-response-handling.md) | 目标端：同一个请求只执行一次 |
| 56 | [响应超时、取消与迟到丢弃](56-response-completion-timeouts-cancellation-and-late-response-discard.md) | 调用端：超时和迟到响应怎么处理 |
| 57 | [响应排序与重复处理](57-response-ordering-stale-suppression-and-duplicate-handling.md) | 响应分类：正常、迟到、过期、重复 |
| 58 | [响应历史与关联回收](58-response-history-retention-and-correlation-eviction.md) | 请求/响应历史的保留窗口和清理策略 |
| 74 | [Transport 与 Retry TimeProvider 收敛](74-transport-and-retry-timeprovider-convergence.md) | transport 注入延迟和 retry backoff 开始共享统一时间源 |
| 76 | [Host TimeProvider 与 Caller Timeout 收敛](76-host-timeprovider-caller-timeout-and-demo-delay-convergence.md) | host/demo 侧的等待和 caller timeout 开始共享统一时间源 |
| 77 | [TimeProvider Timestamp 与 Elapsed 收敛](77-timeprovider-timestamp-and-elapsed-observation-convergence.md) | elapsed 观测开始共享同一时间语义，不再依赖裸 Stopwatch |

### 二、调度与并发

分析：

| 编号 | 文档 | 简介 |
|------|------|------|
| 05 | [ActivationData：复杂度中心](05-activationdata-complexity-center.md) | 为什么 ActivationData 会变成复杂度枢纽 |
| 06 | [调度、重入与交错执行](06-scheduling-reentrancy-interleaving.md) | Turn-based 调度的完整规则 |

复刻设计：

| 编号 | 文档 | 简介 |
|------|------|------|
| 68 | [Grain Interleaving 元数据](68-grain-interleaving-metadata-and-allow-interleaving-turns.md) | 方法级 Interleaving：某些方法可以并发执行 |
| 69 | [Request-Chain Reentrancy](69-request-chain-reentrancy-and-self-call-deadlock-avoidance.md) | 调用链重入：防止 Grain 自己调用自己时死锁 |
| 70 | [Observer 回调重入](70-observer-callback-reentrancy-and-cross-node-chain-reentry.md) | Observer 回调保持调用链上下文，跨节点不死锁 |
| 75 | [ActivationExecutionContext TimeProvider](75-activation-execution-context-timeprovider-and-grain-delay-convergence.md) | 统一时间源开始进入 grain 执行体 |

### 三、目录、放置与路由

分析：

| 编号 | 文档 | 简介 |
|------|------|------|
| 07 | [目录、放置与路由](07-directory-placement-and-routing.md) | 怎么把消息送到正确的节点 |

复刻设计：

| 编号 | 文档 | 简介 |
|------|------|------|
| 40 | [从单节点到多节点计划](40-single-node-to-multi-node-plan.md) | GrainId 稳定性、目录、路由、Owner 模型 |
| 41 | [分布式目录与 Owner 模型](41-distributed-directory-and-owner-model.md) | 目录/Owner/Locator/Routing 的职责分离 |
| 50 | [放置策略与再均衡](50-placement-policy-rebalancing-and-handoff.md) | 初始放置 → 再均衡 → Handoff 三层职责 |
| 67 | [Grain Placement Hint 元数据](67-grain-placement-hint-metadata-and-prefer-local-policy.md) | PreferLocal 放置提示 |

### 四、Membership（集群成员管理）

分析：

| 编号 | 文档 | 简介 |
|------|------|------|
| 10 | [Membership 与集群管理](10-membership-and-cluster-management.md) | Membership 表、Silo 状态、故障探测 |

复刻设计：

| 编号 | 文档 | 简介 |
|------|------|------|
| 44 | [Membership、健康与迁移策略](44-membership-health-and-relocation-policy.md) | Membership ≠ 节点健康 ≠ 调用失败：三个独立关注点 |
| 45 | [Membership Epoch、视图变更与故障检测](45-membership-epochs-view-changes-and-failure-detection.md) | Epoch、视图变更记录、故障检测证据 |
| 46 | [Membership 探测、Gossip 与稳定化](46-membership-probing-gossip-and-stabilization.md) | 探测（发现）→ Gossip（传播）→ 稳定化（别过激反应） |
| 47 | [Membership 传播与反熵](47-membership-dissemination-and-anti-entropy.md) | 全量广播 vs 部分扇出 vs 反熵对齐 |
| 48 | [Membership 快照与恢复](48-membership-snapshots-checkpoint-and-recovery.md) | Membership 检查点与重启恢复 |
| 73 | [Membership TimeProvider 收敛](73-membership-timeprovider-convergence-and-deterministic-view-change-timestamps.md) | View Change 时间戳开始共享统一时间源 |

### 五、Handoff（Grain 搬迁）

复刻设计（本主题在分析阶段没有独立文档）：

| 编号 | 文档 | 简介 |
|------|------|------|
| 51 | [放置状态迁移与 Warm Handoff](51-placement-state-transfer-and-warm-handoff.md) | Warm Handoff：带状态搬迁的设计边界 |
| 52 | [Handoff 失败回退](52-handoff-failure-rollback-and-fallback.md) | Handoff 失败分类：capture/stage/apply 各种失败怎么退回 |
| 53 | [Handoff 排序、静止与排空](53-handoff-ordering-quiescence-and-drain.md) | 先停收新请求 → 等旧请求跑完 → 再抓状态 |
| 54 | [Handoff 围栏与过期消息拒绝](54-handoff-fencing-epochs-and-stale-message-rejection.md) | Owner Epoch 和 Fencing Token |

### 六、回调与 Observer

分析：

| 编号 | 文档 | 简介 |
|------|------|------|
| 11 | [客户端网关与回调](11-client-gateway-and-callbacks.md) | 客户端回调生命周期 |
| 23 | [Grain 扩展、Observer 与回调](23-grain-extensions-observers-and-callbacks.md) | 三种引用类型的不同语义 |

复刻设计：

| 编号 | 文档 | 简介 |
|------|------|------|
| 59 | [回调目标与 Observer](59-callback-targets-observers-and-cross-node-response-channel.md) | 反向调用通道：通过消息系统实现回调 |
| 60 | [透明 Observer 引用](60-transparent-observer-references-and-object-reference-boundaries.md) | 类型化的 Observer 引用：不只是原始句柄 |
| 61 | [对象引用表示与再水化](61-object-reference-representation-and-rehydration.md) | 对象引用跨边界是序列化数据，不是对象标识 |
| 62 | [对象引用工厂注册表](62-object-reference-factory-registry-and-generated-rehydration.md) | 统一的对象引用创建和再水化工厂 |

### 七、持久化与状态

分析：

| 编号 | 文档 | 简介 |
|------|------|------|
| 08 | [持久化状态与存储](08-persistent-state-and-storage.md) | IPersistentState、StateStorageBridge、ETag 并发 |

### 八、Timer 与 Reminder

分析：

| 编号 | 文档 | 简介 |
|------|------|------|
| 09 | [Timer 与 Reminder](09-timers-and-reminders.md) | 本地 Timer vs 持久化 Reminder：两条独立的时间链 |

复刻设计：

| 编号 | 文档 | 简介 |
|------|------|------|
| 71 | [Activation 级 Timer](71-activation-owned-timers-and-timer-turn-classification.md) | Timer 归属 Activation，随 Activation 销毁而取消 |
| 72 | [Activation TimeProvider 收敛](72-activation-timeprovider-convergence-and-controlled-time-validation.md) | Activation 本地时间语义统一：Timer、LastTouchedUtc、Idle Collect 都用同一个 TimeProvider |
| 73 | [Membership TimeProvider 收敛](73-membership-timeprovider-convergence-and-deterministic-view-change-timestamps.md) | Membership View Change 时间戳开始共享统一时间源 |
| 74 | [Transport 与 Retry TimeProvider 收敛](74-transport-and-retry-timeprovider-convergence.md) | request/response 控制路径上的 delay 开始共享统一时间源 |
| 75 | [ActivationExecutionContext TimeProvider](75-activation-execution-context-timeprovider-and-grain-delay-convergence.md) | activation 内 grain 自己的 slow delay 开始共享统一时间源 |
| 76 | [Host TimeProvider 与 Caller Timeout 收敛](76-host-timeprovider-caller-timeout-and-demo-delay-convergence.md) | host/demo 侧的等待和 caller timeout 开始共享统一时间源 |
| 77 | [TimeProvider Timestamp 与 Elapsed 收敛](77-timeprovider-timestamp-and-elapsed-observation-convergence.md) | manual time 现在也能提供一致的 elapsed/timestamp 观测 |

### 九、生命周期与回收

分析：

| 编号 | 文档 | 简介 |
|------|------|--------|
| 14 | [Grain 生命周期与回收](14-grain-lifecycle-and-collection.md) | 创建 → 激活 → 空闲回收 → 停用 → 迁移 → 清理 |

复刻设计：

| 编号 | 文档 | 简介 |
|------|------|--------|
| 66 | [Grain 回收时间元数据](66-grain-collection-age-metadata-and-idle-collection-policy.md) | 不同 Grain 类型可以配置不同的空闲回收时间 |
| 49 | [目录状态检查点与 Activation 恢复](49-directory-state-checkpoint-and-activation-recovery.md) | 重启后如何恢复 Grain Directory 和 Activation 元数据 |

### 十、元数据与代码生成

分析：

| 编号 | 文档 | 简介 |
|------|------|--------|
| 03 | [代码生成：代理、Invokable、序列化器](03-codegen-proxy-invokable-serializer.md) | 编译期代码生成管线 |

复刻设计：

| 编号 | 文档 | 简介 |
|------|------|--------|
| 63 | [生成的对象引用元数据](63-generated-object-reference-metadata-and-auto-registration.md) | 生成 Attribute + 程序集扫描 → 自动注册对象引用工厂 |
| 64 | [生成的 Grain Reference 元数据](64-generated-grain-reference-metadata-and-contract-binding-discovery.md) | 自动发现 Grain 接口绑定 |
| 65 | [生成的 Grain 实现元数据](65-generated-grain-implementation-metadata-and-activator-discovery.md) | 自动发现 Grain Activator 工厂 |

### 十一、Streaming、事务与高级能力

分析：

| 编号 | 文档 | 简介 |
|------|------|--------|
| 12 | [Streaming、Pub/Sub 与队列适配器](12-streaming-pubsub-and-queue-adapters.md) | 流系统：Provider、PubSub Grain、Queue Adapter、Pulling Agent |
| 15 | [事务与分布式一致性](15-transactions-and-distributed-consistency.md) | 事务链路：TransactionAgent、参与者、读写锁、日志恢复 |
| 27 | [持久流 Provider 对比](27-persistent-stream-providers-compared.md) | EventHub、Azure Queue、SQS、NATS、Memory 流 Provider 横向对比 |
| 34 | [事件溯源与日志一致性](34-event-sourcing-and-log-consistency.md) | JournaledGrain、LogViewAdaptor、跨集群日志同步 |
| 35 | [Durable Jobs 与 Journaling](35-durable-jobs-and-journaling.md) | 时间分片调度和基于日志的持久化状态机 |

### 十二、宿主、构建与部署

分析：

| 编号 | 文档 | 简介 |
|------|------|--------|
| 13 | [Provider 程序集与默认服务](13-provider-assembly-and-default-services.md) | 默认服务注册、Provider 发现、Manifest 加载 |
| 21 | [宿主启动与部署链](21-host-startup-and-deployment-chain.md) | UseOrleans、生命周期阶段、如何把运行时拉起来 |

复刻设计：

| 编号 | 文档 | 简介 |
|------|------|--------|
| 43 | [故障路径、超时与重试](43-failure-paths-timeouts-and-retries.md) | 超时、远端故障、过期目录、重试——分层处理 |

### 十三、兼容性、版本演进与运维

分析：

| 编号 | 文档 | 简介 |
|------|------|--------|
| 18 | [序列化兼容与版本化](18-serialization-compatibility-and-versioning.md) | 跨版本序列化兼容、字段 ID、别名 |
| 19 | [代码生成兼容与遗留](19-codegen-compatibility-and-legacy.md) | 旧版代码生成路径和向后兼容 |
| 25 | [滚动升级与跨版本运行](25-rolling-upgrade-and-cross-version-operation.md) | 版本选择、兼容 Director、新旧 Activation 共存 |
| 26 | [多集群与地理分布](26-multi-cluster-and-geo-distribution.md) | Orleans 多集群能力的实际状态 |

### 十四、安全、可观测性与管理

分析：

| 编号 | 文档 | 简介 |
|------|------|--------|
| 16 | [诊断、遥测与统计](16-diagnostics-telemetry-and-statistics.md) | 日志、指标、Trace、诊断事件 |
| 22 | [安全与认证边界](22-security-and-auth-boundaries.md) | TLS、证书认证、连接安全 |
| 24 | [管理面与 System Target](24-management-plane-and-system-targets.md) | ManagementGrain、SystemTarget、内部控制路径 |
| 20 | [资源治理与负载均衡](20-resource-governance-and-load-balancing.md) | 资源治理、负载脱落、限流 |

### 十五、工程基建

分析：

| 编号 | 文档 | 简介 |
|------|------|--------|
| 17 | [测试基建与 TestCluster](17-testing-infrastructure-and-testcluster.md) | TestCluster、测试工具、行为验证 |
| 28 | [AOT 裁剪与平台兼容](28-aot-trimming-and-platform-compatibility.md) | Source Generator 对 AOT 的帮助和硬边界 |
| 30 | [构建管线与贡献工作流](30-build-pipeline-and-contributor-workflow.md) | 构建/测试/打包管线 |
| 31 | [基准测试与性能](31-benchmarks-and-performance-testing.md) | 性能热点关注区域 |
| 32 | [故障注入与恢复路径](32-fault-injection-and-recovery-paths.md) | 故障注入层、恢复协议 |
| 36 | [序列化适配器与外部 Codec 生态](36-serialization-adapters-and-external-codec-ecosystem.md) | JSON、MessagePack、MemoryPack、Protobuf 等外部 Codec 怎么接入 |

### 十六、总体规划与路线图

| 编号 | 文档 | 简介 |
|------|------|--------|
| 01 | [Orleans 源码总览](01-orleans-source-overview.md) | 全局地图和分层理解 |
| 29 | [源码树与项目依赖图](29-source-tree-and-project-dependency-map.md) | 仓库目录结构和项目间依赖 |
| 33 | [最小复刻切口与路线图](33-minimal-replica-cut-and-roadmap.md) | 承上启下的关键文档 |
| 37 | [总索引与阅读地图](37-master-index-reading-map-and-replica-roadmap.md) | 01-36 篇的主题归类 |
| 38 | [复刻设计总纲](38-replica-design-blueprint.md) | 新系统架构蓝图 |

---

## 文档编号速查

| 编号 | 标题 | 阶段 |
|------|------|------|
| 01 | Orleans 源码总览 | 分析 |
| 02 | 从 GetGrain 到方法返回 | 分析 |
| 03 | 代码生成：代理、Invokable、序列化器 | 分析 |
| 04 | 序列化运行时链路 | 分析 |
| 05 | ActivationData：复杂度中心 | 分析 |
| 06 | 调度、重入与交错执行 | 分析 |
| 07 | 目录、放置与路由 | 分析 |
| 08 | 持久化状态与存储 | 分析 |
| 09 | Timer 与 Reminder | 分析 |
| 10 | Membership 与集群管理 | 分析 |
| 11 | 客户端网关与回调 | 分析 |
| 12 | Streaming、Pub/Sub 与队列适配器 | 分析 |
| 13 | Provider 程序集与默认服务 | 分析 |
| 14 | Grain 生命周期与回收 | 分析 |
| 15 | 事务与分布式一致性 | 分析 |
| 16 | 诊断、遥测与统计 | 分析 |
| 17 | 测试基建与 TestCluster | 分析 |
| 18 | 序列化兼容与版本化 | 分析 |
| 19 | 代码生成兼容与遗留 | 分析 |
| 20 | 资源治理与负载均衡 | 分析 |
| 21 | 宿主启动与部署链 | 分析 |
| 22 | 安全与认证边界 | 分析 |
| 23 | Grain 扩展、Observer 与回调 | 分析 |
| 24 | 管理面与 System Target | 分析 |
| 25 | 滚动升级与跨版本运行 | 分析 |
| 26 | 多集群与地理分布 | 分析 |
| 27 | 持久流 Provider 对比 | 分析 |
| 28 | AOT 裁剪与平台兼容 | 分析 |
| 29 | 源码树与项目依赖图 | 分析 |
| 30 | 构建管线与贡献工作流 | 分析 |
| 31 | 基准测试与性能 | 分析 |
| 32 | 故障注入与恢复路径 | 分析 |
| 33 | 最小复刻切口与路线图 | 分析 |
| 34 | 事件溯源与日志一致性 | 分析 |
| 35 | Durable Jobs 与 Journaling | 分析 |
| 36 | 序列化适配器与外部 Codec 生态 | 分析 |
| 37 | 总索引与阅读地图 | 分析 |
| 38 | 复刻设计总纲 | 设计 |
| 39 | 第一版运行时实现计划 | 设计 |
| 40 | 从单节点到多节点计划 | 设计 |
| 41 | 分布式目录与 Owner 模型 | 设计 |
| 42 | 远程转发与消息循环 | 设计 |
| 43 | 故障路径、超时与重试 | 设计 |
| 44 | Membership、健康与迁移策略 | 设计 |
| 45 | Membership Epoch、视图变更与故障检测 | 设计 |
| 46 | Membership 探测、Gossip 与稳定化 | 设计 |
| 47 | Membership 传播与反熵 | 设计 |
| 48 | Membership 快照与恢复 | 设计 |
| 49 | 目录状态检查点与 Activation 恢复 | 设计 |
| 50 | 放置策略与再均衡 | 设计 |
| 51 | 放置状态迁移与 Warm Handoff | 设计 |
| 52 | Handoff 失败回退 | 设计 |
| 53 | Handoff 排序、静止与排空 | 设计 |
| 54 | Handoff 围栏与过期消息拒绝 | 设计 |
| 55 | 请求去重与过期响应处理 | 设计 |
| 56 | 响应超时、取消与迟到丢弃 | 设计 |
| 57 | 响应排序与重复处理 | 设计 |
| 58 | 响应历史与关联回收 | 设计 |
| 59 | 回调目标与 Observer | 设计 |
| 60 | 透明 Observer 引用 | 设计 |
| 61 | 对象引用表示与再水化 | 设计 |
| 62 | 对象引用工厂注册表 | 设计 |
| 63 | 生成的对象引用元数据 | 设计 |
| 64 | 生成的 Grain Reference 元数据 | 设计 |
| 65 | 生成的 Grain 实现元数据 | 设计 |
| 66 | Grain 回收时间元数据 | 设计 |
| 67 | Grain Placement Hint 元数据 | 设计 |
| 68 | Grain Interleaving 元数据 | 设计 |
| 69 | Request-Chain Reentrancy | 设计 |
| 70 | Observer 回调重入 | 设计 |
| 71 | Activation 级 Timer | 设计 |
| 72 | Activation TimeProvider 收敛 | 设计 |
| 73 | Membership TimeProvider 收敛 | 设计 |
| 74 | Transport 与 Retry TimeProvider 收敛 | 设计 |
| 75 | ActivationExecutionContext TimeProvider | 设计 |
| 76 | Host TimeProvider 与 Caller Timeout 收敛 | 设计 |
| 77 | TimeProvider Timestamp 与 Elapsed 收敛 | 设计 |
