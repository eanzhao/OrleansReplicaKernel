# 总索引、阅读地图与复刻路线总图：Orleans 1-36 篇怎么串起来（第三十七篇）

这一篇不是另一篇专题文档。

它是前面 1 到 36 篇的索引，也是后面继续往下读、往下拆、往下重建时的起点。

如果你已经被前面的细节绕晕了，先别急着往回翻。先看这篇，把大图记住，再回去补局部。

先把结论说直白一点：

- Orleans 这套源码梳理，真正的主干很少，核心就是“引用 -> 消息 -> 序列化 -> 激活 -> 调度 -> 返回”。
- 目录、membership、streaming、事务、诊断、测试、兼容、AOT、构建链，都是围绕主干长出来的外圈。
- 如果目标是重建一版，不应该把外圈一起抄下来，而应该先抓主干，再逐层往外加能力。
- 这 36 篇里，`01`、`02`、`03`、`04`、`05`、`06`、`07`、`11`、`14`、`21`、`29`、`33` 是最值得先吃透的。

如果压成一句话，就是：

> 先记住 Orleans 的骨架，再决定你要抄哪些器官、哪些皮肤、哪些老伤疤。

---

## 1. 这张地图怎么用

### 先读这 12 篇

如果你是第一次进这套源码，先读这些：

1. [01-orleans-source-overview.md](./01-orleans-source-overview.md)
1. [29-source-tree-and-project-dependency-map.md](./29-source-tree-and-project-dependency-map.md)
1. [02-getgrain-to-method-return.md](./02-getgrain-to-method-return.md)
1. [03-codegen-proxy-invokable-serializer.md](./03-codegen-proxy-invokable-serializer.md)
1. [04-serialization-runtime-chain.md](./04-serialization-runtime-chain.md)
1. [05-activationdata-complexity-center.md](./05-activationdata-complexity-center.md)
1. [06-scheduling-reentrancy-interleaving.md](./06-scheduling-reentrancy-interleaving.md)
1. [07-directory-placement-and-routing.md](./07-directory-placement-and-routing.md)
1. [11-client-gateway-and-callbacks.md](./11-client-gateway-and-callbacks.md)
1. [14-grain-lifecycle-and-collection.md](./14-grain-lifecycle-and-collection.md)
1. [21-host-startup-and-deployment-chain.md](./21-host-startup-and-deployment-chain.md)
1. [33-minimal-replica-cut-and-roadmap.md](./33-minimal-replica-cut-and-roadmap.md)

这 12 篇把 Orleans 的主干先搭起来了。

### 先放一边的篇

下面这些很重要，但适合在主干通了以后再看：

1. [08-persistent-state-and-storage.md](./08-persistent-state-and-storage.md)
1. [09-timers-and-reminders.md](./09-timers-and-reminders.md)
1. [10-membership-and-cluster-management.md](./10-membership-and-cluster-management.md)
1. [12-streaming-pubsub-and-queue-adapters.md](./12-streaming-pubsub-and-queue-adapters.md)
1. [13-provider-assembly-and-default-services.md](./13-provider-assembly-and-default-services.md)
1. [15-transactions-and-distributed-consistency.md](./15-transactions-and-distributed-consistency.md)
1. [16-diagnostics-telemetry-and-statistics.md](./16-diagnostics-telemetry-and-statistics.md)
1. [17-testing-infrastructure-and-testcluster.md](./17-testing-infrastructure-and-testcluster.md)
1. [18-serialization-compatibility-and-versioning.md](./18-serialization-compatibility-and-versioning.md)
1. [19-codegen-compatibility-and-legacy.md](./19-codegen-compatibility-and-legacy.md)
1. [20-resource-governance-and-load-balancing.md](./20-resource-governance-and-load-balancing.md)
1. [22-security-and-auth-boundaries.md](./22-security-and-auth-boundaries.md)
1. [23-grain-extensions-observers-and-callbacks.md](./23-grain-extensions-observers-and-callbacks.md)
1. [24-management-plane-and-system-targets.md](./24-management-plane-and-system-targets.md)
1. [25-rolling-upgrade-and-cross-version-operation.md](./25-rolling-upgrade-and-cross-version-operation.md)
1. [26-multi-cluster-and-geo-distribution.md](./26-multi-cluster-and-geo-distribution.md)
1. [27-persistent-stream-providers-compared.md](./27-persistent-stream-providers-compared.md)
1. [28-aot-trimming-and-platform-compatibility.md](./28-aot-trimming-and-platform-compatibility.md)
1. [34-event-sourcing-and-log-consistency.md](./34-event-sourcing-and-log-consistency.md)
1. [35-durable-jobs-and-journaling.md](./35-durable-jobs-and-journaling.md)
1. [36-serialization-adapters-and-external-codec-ecosystem.md](./36-serialization-adapters-and-external-codec-ecosystem.md)
1. [30-build-pipeline-and-contributor-workflow.md](./30-build-pipeline-and-contributor-workflow.md)
1. [31-benchmarks-and-performance-testing.md](./31-benchmarks-and-performance-testing.md)
1. [32-fault-injection-and-recovery-paths.md](./32-fault-injection-and-recovery-paths.md)

这些篇不是外围废话，只是它们更像“主干之外的能力层”。

---

## 2. 按主题归类

### 主干调用链

- [01-orleans-source-overview.md](./01-orleans-source-overview.md)：总览图，先把仓库分层记住。
- [02-getgrain-to-method-return.md](./02-getgrain-to-method-return.md)：从 `GetGrain` 到方法返回的一次完整链路。
- [03-codegen-proxy-invokable-serializer.md](./03-codegen-proxy-invokable-serializer.md)：编译期怎么把代理、Invokable、Serializer 拼起来。
- [04-serialization-runtime-chain.md](./04-serialization-runtime-chain.md)：运行时序列化怎么把 `CodecProvider`、`TypeConverter`、`MessageSerializer` 接起来。
- [05-activationdata-complexity-center.md](./05-activationdata-complexity-center.md)：为什么 `ActivationData` 会变成复杂度中心。
- [06-scheduling-reentrancy-interleaving.md](./06-scheduling-reentrancy-interleaving.md)：调度、串行、重入、插队到底怎么回事。
- [07-directory-placement-and-routing.md](./07-directory-placement-and-routing.md)：目录、放置、路由、缓存怎么串。
- [11-client-gateway-and-callbacks.md](./11-client-gateway-and-callbacks.md)：client 侧如何走 gateway 和 callback。
- [14-grain-lifecycle-and-collection.md](./14-grain-lifecycle-and-collection.md)：激活、退场、回收、迁移怎么收尾。
- [21-host-startup-and-deployment-chain.md](./21-host-startup-and-deployment-chain.md)：宿主、builder、lifecycle、默认服务如何把 runtime 拉起来。

### 集群与控制面

- [10-membership-and-cluster-management.md](./10-membership-and-cluster-management.md)：membership table、silo 状态、故障探测。
- [20-resource-governance-and-load-balancing.md](./20-resource-governance-and-load-balancing.md)：统计结果如何反过来影响调度和放置。
- [24-management-plane-and-system-targets.md](./24-management-plane-and-system-targets.md)：管理面和 `SystemTarget` 怎么把内部控制命令送进去。
- [25-rolling-upgrade-and-cross-version-operation.md](./25-rolling-upgrade-and-cross-version-operation.md)：滚动升级和跨版本共存怎么跑。
- [26-multi-cluster-and-geo-distribution.md](./26-multi-cluster-and-geo-distribution.md)：多集群能力在源码里到底有多实。

### 能力模块

- [08-persistent-state-and-storage.md](./08-persistent-state-and-storage.md)：持久化状态与 storage provider。
- [09-timers-and-reminders.md](./09-timers-and-reminders.md)：本地 timer 和持久 reminder 的区别。
- [12-streaming-pubsub-and-queue-adapters.md](./12-streaming-pubsub-and-queue-adapters.md)：streaming、pub/sub、queue adapter。
- [13-provider-assembly-and-default-services.md](./13-provider-assembly-and-default-services.md)：provider 装配与默认服务注册。
- [15-transactions-and-distributed-consistency.md](./15-transactions-and-distributed-consistency.md)：事务链路到底怎么拼。
- [16-diagnostics-telemetry-and-statistics.md](./16-diagnostics-telemetry-and-statistics.md)：日志、指标、trace、诊断事件。
- [18-serialization-compatibility-and-versioning.md](./18-serialization-compatibility-and-versioning.md)：field id、alias、兼容反序列化。
- [19-codegen-compatibility-and-legacy.md](./19-codegen-compatibility-and-legacy.md)：代码生成里的兼容层和历史包袱。
- [22-security-and-auth-boundaries.md](./22-security-and-auth-boundaries.md)：连接安全和认证边界。
- [23-grain-extensions-observers-and-callbacks.md](./23-grain-extensions-observers-and-callbacks.md)：扩展、observer、回调对象的引用语义。
- [27-persistent-stream-providers-compared.md](./27-persistent-stream-providers-compared.md)：持久流 provider 横向对比。
- [28-aot-trimming-and-platform-compatibility.md](./28-aot-trimming-and-platform-compatibility.md)：AOT、裁剪与平台兼容。
- [34-event-sourcing-and-log-consistency.md](./34-event-sourcing-and-log-consistency.md)：`EventSourcing` 和 log consistency 这条独立能力线。
- [35-durable-jobs-and-journaling.md](./35-durable-jobs-and-journaling.md)：`DurableJobs` 和 `Journaling` 这两套边缘但很能说明 Orleans 风格的子系统。
- [36-serialization-adapters-and-external-codec-ecosystem.md](./36-serialization-adapters-and-external-codec-ecosystem.md)：外部 serializer / codec 适配生态怎么接进主链。

### 工程与验证

- [17-testing-infrastructure-and-testcluster.md](./17-testing-infrastructure-and-testcluster.md)：测试基建和 `TestCluster`。
- [30-build-pipeline-and-contributor-workflow.md](./30-build-pipeline-and-contributor-workflow.md)：构建链和贡献工作流。
- [31-benchmarks-and-performance-testing.md](./31-benchmarks-and-performance-testing.md)：性能测试和热点关注。
- [32-fault-injection-and-recovery-paths.md](./32-fault-injection-and-recovery-paths.md)：故障注入与恢复路径。

### 重建路线

- [33-minimal-replica-cut-and-roadmap.md](./33-minimal-replica-cut-and-roadmap.md)：如果不是复刻，而是重建一版，应该怎么取舍。

---

## 3. 如果你只想快速读一遍

按这个顺序最稳：

1. [01-orleans-source-overview.md](./01-orleans-source-overview.md)
1. [29-source-tree-and-project-dependency-map.md](./29-source-tree-and-project-dependency-map.md)
1. [02-getgrain-to-method-return.md](./02-getgrain-to-method-return.md)
1. [03-codegen-proxy-invokable-serializer.md](./03-codegen-proxy-invokable-serializer.md)
1. [04-serialization-runtime-chain.md](./04-serialization-runtime-chain.md)
1. [05-activationdata-complexity-center.md](./05-activationdata-complexity-center.md)
1. [06-scheduling-reentrancy-interleaving.md](./06-scheduling-reentrancy-interleaving.md)
1. [07-directory-placement-and-routing.md](./07-directory-placement-and-routing.md)
1. [11-client-gateway-and-callbacks.md](./11-client-gateway-and-callbacks.md)
1. [14-grain-lifecycle-and-collection.md](./14-grain-lifecycle-and-collection.md)
1. [21-host-startup-and-deployment-chain.md](./21-host-startup-and-deployment-chain.md)
1. [33-minimal-replica-cut-and-roadmap.md](./33-minimal-replica-cut-and-roadmap.md)

这条线读完，你就已经知道 Orleans 的主干怎么跑了。

如果你还想往外扩，再按这个顺序补：

1. [10-membership-and-cluster-management.md](./10-membership-and-cluster-management.md)
1. [12-streaming-pubsub-and-queue-adapters.md](./12-streaming-pubsub-and-queue-adapters.md)
1. [13-provider-assembly-and-default-services.md](./13-provider-assembly-and-default-services.md)
1. [15-transactions-and-distributed-consistency.md](./15-transactions-and-distributed-consistency.md)
1. [16-diagnostics-telemetry-and-statistics.md](./16-diagnostics-telemetry-and-statistics.md)
1. [17-testing-infrastructure-and-testcluster.md](./17-testing-infrastructure-and-testcluster.md)
1. [18-serialization-compatibility-and-versioning.md](./18-serialization-compatibility-and-versioning.md)
1. [19-codegen-compatibility-and-legacy.md](./19-codegen-compatibility-and-legacy.md)
1. [20-resource-governance-and-load-balancing.md](./20-resource-governance-and-load-balancing.md)
1. [22-security-and-auth-boundaries.md](./22-security-and-auth-boundaries.md)
1. [23-grain-extensions-observers-and-callbacks.md](./23-grain-extensions-observers-and-callbacks.md)
1. [24-management-plane-and-system-targets.md](./24-management-plane-and-system-targets.md)
1. [25-rolling-upgrade-and-cross-version-operation.md](./25-rolling-upgrade-and-cross-version-operation.md)
1. [26-multi-cluster-and-geo-distribution.md](./26-multi-cluster-and-geo-distribution.md)
1. [27-persistent-stream-providers-compared.md](./27-persistent-stream-providers-compared.md)
1. [28-aot-trimming-and-platform-compatibility.md](./28-aot-trimming-and-platform-compatibility.md)
1. [34-event-sourcing-and-log-consistency.md](./34-event-sourcing-and-log-consistency.md)
1. [35-durable-jobs-and-journaling.md](./35-durable-jobs-and-journaling.md)
1. [36-serialization-adapters-and-external-codec-ecosystem.md](./36-serialization-adapters-and-external-codec-ecosystem.md)
1. [30-build-pipeline-and-contributor-workflow.md](./30-build-pipeline-and-contributor-workflow.md)
1. [31-benchmarks-and-performance-testing.md](./31-benchmarks-and-performance-testing.md)
1. [32-fault-injection-and-recovery-paths.md](./32-fault-injection-and-recovery-paths.md)

---

## 4. 如果你要重建一版，应该按什么阶段做

这部分我建议直接照 [33-minimal-replica-cut-and-roadmap.md](./33-minimal-replica-cut-and-roadmap.md) 的思路走，但再压得更工程化一点。

### 阶段 0：先定骨架

先定这些东西：

- 身份
- 引用
- 调用协议
- 消息 envelope
- 类型 manifest
- 编译期生成入口

对应先看：

- [02-getgrain-to-method-return.md](./02-getgrain-to-method-return.md)
- [03-codegen-proxy-invokable-serializer.md](./03-codegen-proxy-invokable-serializer.md)
- [04-serialization-runtime-chain.md](./04-serialization-runtime-chain.md)
- [33-minimal-replica-cut-and-roadmap.md](./33-minimal-replica-cut-and-roadmap.md)

### 阶段 1：跑通单机内核

先做：

- activation
- mailbox
- turn scheduler
- host/lifecycle
- loopback 或 in-memory transport

对应先看：

- [05-activationdata-complexity-center.md](./05-activationdata-complexity-center.md)
- [06-scheduling-reentrancy-interleaving.md](./06-scheduling-reentrancy-interleaving.md)
- [14-grain-lifecycle-and-collection.md](./14-grain-lifecycle-and-collection.md)
- [21-host-startup-and-deployment-chain.md](./21-host-startup-and-deployment-chain.md)

### 阶段 2：补单集群运行时

再加：

- directory
- placement
- membership
- client/gateway
- state storage
- timer/reminder

对应先看：

- [07-directory-placement-and-routing.md](./07-directory-placement-and-routing.md)
- [10-membership-and-cluster-management.md](./10-membership-and-cluster-management.md)
- [11-client-gateway-and-callbacks.md](./11-client-gateway-and-callbacks.md)
- [08-persistent-state-and-storage.md](./08-persistent-state-and-storage.md)
- [09-timers-and-reminders.md](./09-timers-and-reminders.md)

### 阶段 3：补能力模块

再加：

- streaming
- transactions
- security
- diagnostics
- extensions / observer

对应先看：

- [12-streaming-pubsub-and-queue-adapters.md](./12-streaming-pubsub-and-queue-adapters.md)
- [13-provider-assembly-and-default-services.md](./13-provider-assembly-and-default-services.md)
- [15-transactions-and-distributed-consistency.md](./15-transactions-and-distributed-consistency.md)
- [16-diagnostics-telemetry-and-statistics.md](./16-diagnostics-telemetry-and-statistics.md)
- [22-security-and-auth-boundaries.md](./22-security-and-auth-boundaries.md)
- [23-grain-extensions-observers-and-callbacks.md](./23-grain-extensions-observers-and-callbacks.md)

### 阶段 4：补运维和演进

再补：

- compatibility
- rolling upgrade
- resource governance
- multi-cluster
- AOT / trimming
- benchmark / test infrastructure

对应先看：

- [18-serialization-compatibility-and-versioning.md](./18-serialization-compatibility-and-versioning.md)
- [19-codegen-compatibility-and-legacy.md](./19-codegen-compatibility-and-legacy.md)
- [20-resource-governance-and-load-balancing.md](./20-resource-governance-and-load-balancing.md)
- [25-rolling-upgrade-and-cross-version-operation.md](./25-rolling-upgrade-and-cross-version-operation.md)
- [26-multi-cluster-and-geo-distribution.md](./26-multi-cluster-and-geo-distribution.md)
- [28-aot-trimming-and-platform-compatibility.md](./28-aot-trimming-and-platform-compatibility.md)
- [30-build-pipeline-and-contributor-workflow.md](./30-build-pipeline-and-contributor-workflow.md)
- [31-benchmarks-and-performance-testing.md](./31-benchmarks-and-performance-testing.md)
- [32-fault-injection-and-recovery-paths.md](./32-fault-injection-and-recovery-paths.md)

---

## 5. 主干和外围，怎么分

### 主干

这批是“你不看就很难真正懂 Orleans”的：

- [01-orleans-source-overview.md](./01-orleans-source-overview.md)
- [02-getgrain-to-method-return.md](./02-getgrain-to-method-return.md)
- [03-codegen-proxy-invokable-serializer.md](./03-codegen-proxy-invokable-serializer.md)
- [04-serialization-runtime-chain.md](./04-serialization-runtime-chain.md)
- [05-activationdata-complexity-center.md](./05-activationdata-complexity-center.md)
- [06-scheduling-reentrancy-interleaving.md](./06-scheduling-reentrancy-interleaving.md)
- [07-directory-placement-and-routing.md](./07-directory-placement-and-routing.md)
- [11-client-gateway-and-callbacks.md](./11-client-gateway-and-callbacks.md)
- [14-grain-lifecycle-and-collection.md](./14-grain-lifecycle-and-collection.md)
- [21-host-startup-and-deployment-chain.md](./21-host-startup-and-deployment-chain.md)
- [29-source-tree-and-project-dependency-map.md](./29-source-tree-and-project-dependency-map.md)
- [33-minimal-replica-cut-and-roadmap.md](./33-minimal-replica-cut-and-roadmap.md)

### 外围

这批不是不重要，而是更适合主干之后再看：

- [08-persistent-state-and-storage.md](./08-persistent-state-and-storage.md)
- [09-timers-and-reminders.md](./09-timers-and-reminders.md)
- [10-membership-and-cluster-management.md](./10-membership-and-cluster-management.md)
- [12-streaming-pubsub-and-queue-adapters.md](./12-streaming-pubsub-and-queue-adapters.md)
- [13-provider-assembly-and-default-services.md](./13-provider-assembly-and-default-services.md)
- [15-transactions-and-distributed-consistency.md](./15-transactions-and-distributed-consistency.md)
- [16-diagnostics-telemetry-and-statistics.md](./16-diagnostics-telemetry-and-statistics.md)
- [17-testing-infrastructure-and-testcluster.md](./17-testing-infrastructure-and-testcluster.md)
- [18-serialization-compatibility-and-versioning.md](./18-serialization-compatibility-and-versioning.md)
- [19-codegen-compatibility-and-legacy.md](./19-codegen-compatibility-and-legacy.md)
- [20-resource-governance-and-load-balancing.md](./20-resource-governance-and-load-balancing.md)
- [22-security-and-auth-boundaries.md](./22-security-and-auth-boundaries.md)
- [23-grain-extensions-observers-and-callbacks.md](./23-grain-extensions-observers-and-callbacks.md)
- [24-management-plane-and-system-targets.md](./24-management-plane-and-system-targets.md)
- [25-rolling-upgrade-and-cross-version-operation.md](./25-rolling-upgrade-and-cross-version-operation.md)
- [26-multi-cluster-and-geo-distribution.md](./26-multi-cluster-and-geo-distribution.md)
- [27-persistent-stream-providers-compared.md](./27-persistent-stream-providers-compared.md)
- [28-aot-trimming-and-platform-compatibility.md](./28-aot-trimming-and-platform-compatibility.md)
- [30-build-pipeline-and-contributor-workflow.md](./30-build-pipeline-and-contributor-workflow.md)
- [31-benchmarks-and-performance-testing.md](./31-benchmarks-and-performance-testing.md)
- [32-fault-injection-and-recovery-paths.md](./32-fault-injection-and-recovery-paths.md)

---

## 6. 最后一句

如果你只记一件事，就记这句：

> Orleans 的主干不是很多篇文档拼出来的，而是少数几条链反复出现，其他东西都围着它们长出来的。

这份总索引的目的，就是把这些链先摆到你眼前。

后面不管你是继续读源码，还是准备重建一版，先从主干下手，别先钻进外围细节里。
