# 67. Grain Placement Hint Metadata 与 Prefer-Local Policy

## 1. 这一篇补哪块

66 把第一类真正会影响运行时行为的 grain class metadata 接进来了：

- `collection age`
- 开始影响 activation 生命周期

这一篇沿同一条路再往前推一步，补的是另一类很适合当前阶段落地的 grain metadata：

`placement hint`

更具体一点，这一版先只补一个很窄、但很有代表性的 hint：

- `PreferLocalPlacement`

也就是：

- placement policy 还是 placement policy
- 但 grain type 可以通过 metadata 告诉 policy：
  - “如果本地 node 健康，我希望优先放本地”

## 2. 为什么这次选 placement hint，而不是直接做完整 placement strategy

如果现在直接上完整 placement manifest，很快就会碰到更大的一坨：

- placement strategy resolver
- per-grain strategy type
- policy provider
- rebalancing compatibility
- grain property merge

这样会把“grain class metadata 已经开始影响 initial placement”这件事本身淹掉。

而 `PreferLocalPlacement` 很适合当前阶段：

1. 足够真实，Orleans 本来就有类似语义
2. 足够独立，不用先引进整套 placement strategy 系统
3. 足够能验证，当前 toy runtime 里一眼就能看出结果
4. 足够能往后长，后面可以自然并到完整 grain manifest

所以这次故意先只做 hint，不做整套 strategy。

## 3. 这一版具体做了什么

当前这版先补了一个很窄的 placement metadata 版本：

1. `GeneratedGrainImplementationAttribute`
   - 新增 `PreferLocalPlacement`
2. builder 扫描 generated grain implementation metadata 时
   - 会把这个 flag 一起收进 grain implementation registration
3. `InMemoryGrainDirectory`
   - 第一次给 grain 选 owner 时
   - 不再只把 `grainId + membershipView + loadSnapshot` 交给 placement policy
   - 还会把 grain type 自己的 placement hint 一起传进去
4. `LeastLoadedPlacementPolicy`
   - 默认情况下，继续走原来的 least-loaded 逻辑
   - 只有在 grain hint 声明 `PreferLocalPlacement = true`，并且本地 node 仍然健康时
   - 才直接优先选本地 node
5. `CounterGrain`
   - 当前 demo 里把这个 hint 打开了
6. `EchoGrain`
   - 继续走默认 least-loaded placement

这样当前 demo 里就出现了很清楚的对比：

- `echo/fresh-placement`
  - 继续按 least-loaded 选到更空的 `dev-node-3`
- `counter/prefer-local-placement`
  - 虽然远端更空，但因为 grain metadata 声明了 prefer local
  - 最后还是放在 `dev-node-1`

## 4. 这层设计为什么故意只给 hint，不让 metadata 直接决定 owner

这点非常重要。

这次不是把 grain metadata 写成：

- “这个 grain 一定要去 node-x”

也不是让 metadata 直接跳过 placement policy。

而是：

- grain metadata 只提供一个 hint
- 真实的选路逻辑仍然由 placement policy 执行

这样做的好处很直接：

1. `grain metadata` 不会直接吞掉 placement policy
2. `placement policy` 仍然保留对健康状态和全局选路语义的控制权
3. 后面再加新的 hint，也不会把目录或 policy 搅成一锅

也就是说，这一版补的不是“grain metadata 直接接管 placement”，而是：

`grain metadata 开始给 placement policy 提供局部偏好。`

## 5. 为什么这比“多了一个 bool”更重要

表面上看，这一轮像是只多了一个：

- `PreferLocalPlacement`

但真正重要的是：

`grain implementation metadata 现在已经开始影响 initial placement 决策。`

这和 66 的 collection age 一起看，说明 grain metadata 已经开始往两个真正的运行时方向扩：

1. 生命周期
2. 放置策略

这比单纯的 activator discovery 更进一步。

因为 activator discovery 还只是“怎么创建”。
而 placement hint 已经是在决定：

- “它该先去哪儿”

这就是 runtime 行为本身了。

## 6. 当前实现故意收得很窄

这一版不是完整 placement manifest。

还没做的包括：

1. 多种 placement strategy 类型
2. per-grain strategy resolver
3. manifest 级 placement property merge
4. rebalancing 侧对 grain hint 的更细粒度消费
5. directory / placement / membership 之间更正式的 strategy contract

所以现在更准确的理解应该是：

`grain metadata 已经可以影响 initial placement，但现在还只是一个 prefer-local hint，不是完整 placement strategy 系统。`

## 7. 这一版最值得看的验证

这一版最值钱的验证，不是单纯看字段有没有扫出来，而是看：

### 7.1 没有 hint 的 grain

比如：

- `echo/fresh-placement`

它应该继续按 least-loaded 走，最后落到更空的 node。

### 7.2 有 prefer-local hint 的 grain

比如：

- `counter/prefer-local-placement`

即便远端更空，也应该优先落到本地 healthy node。

如果这两条同时成立，才说明：

- metadata 已经真的进入 placement 决策
- 但 policy 还没有被 metadata 吞掉

## 8. 对完整复刻 Orleans 的意义

如果把 65、66、67 连起来看，会发现 grain implementation metadata 已经开始覆盖三类事实：

1. 怎么创建
2. 怎么回收
3. 怎么放置

这三类东西原本很容易散在三个地方：

- builder 手工 glue code
- runtime 硬编码
- 局部 demo 逻辑

而现在它们开始一起回到 generated metadata 里。

这正是后面继续长完整 grain manifest 的基础。

否则你后面再补：

- reentrancy
- lifecycle flags
- placement properties
- collection policy

最后只会变成“四五套小注册表并存”，很难收回来。

## 9. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的说法是：

`OrleansReplicaKernel 已经让 generated grain implementation metadata 开始影响 initial placement：grain type 现在可以通过 prefer-local hint 改变第一次落点；但这还只是 placement hint，不是完整 placement strategy manifest。`
