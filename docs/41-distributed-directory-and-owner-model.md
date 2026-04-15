# 分布式目录与 owner 模型：Orleans 里最容易做脏的一层到底该怎么切（第四十一篇）

这一篇承接前面的 [40-single-node-to-multi-node-plan.md](./40-single-node-to-multi-node-plan.md)。

40 讲的是“从当前原型出发，怎么进多节点”。这一篇只盯住其中最绕、也最容易做脏的一层：

> directory、owner、locator、routing、地址失效、重新定位，它们到底是什么关系。

这层如果不先讲透，后面一加多节点，目录就很容易被写成一个又像缓存、又像事实源、又像路由器的怪东西。Orleans 历史上很多复杂度，基本都在这里拧在一起了。

---

## 1. 先说结论

如果我要重建一版 Orleans，我会先把这件事说死：

- 单节点里的 `local activation directory`，是本节点的激活表。
- 多节点里的 `distributed grain directory`，是 grain owner 语义的承载层。
- `locator` 负责“先把目标缩到某个节点语义上”。
- `routing` 负责“这次调用走本地还是远端”。
- `local activation table` 只管本节点 activation 的创建、复用、卸载。
- `distributed directory` 只管 grain 的 owner、失效和重新定位。

这几个东西可以互相配合，但不能混成一个层。

如果混了，后面所有问题都会变脏：

- 本地缓存会被当成全局事实源
- owner 失效会被写成 activation 卸载
- routing 会被迫同时做目录、转发、容错
- directory 会被迫同时做生命周期管理

这就是最常见的架构滑坡。

---

## 2. 目录到底是不是事实源

先把“目录是不是事实源”这个问题说清楚。

### 2.1 单节点里，目录不是“分布式事实源”

在单节点里，`LocalActivationDirectory` 本质上就是一个本地映射：

- `GrainId` 对应一个 activation
- 没有就创建
- 有就复用
- 需要的时候可以卸载或回收

它的事实范围只到本节点。

所以单节点目录是“本地运行时事实”，不是“系统全局事实”。

### 2.2 多节点里，directory 也不应该变成“所有真相都塞进去”

多节点里最合理的说法不是“目录就是事实源”，而是：

- `owner` 记录是 grain 归属的权威信息
- `directory` 是 owner 语义的承载和分发层
- `cache` 是 directory 的本地副本

也就是说，目录里最关键的不是“我这里有没有一个 activation”，而是“当前这个 grain 的 owner 是谁”。

如果 owner 变了，之前的缓存就可能失效。

如果 owner 失效了，目录就要重新定位。

这和本地 activation 表不是一回事。

---

## 3. owner 语义是什么

owner 这个词，在 Orleans 里经常被说得很轻，但它其实很关键。

### 3.1 owner 不是 activation 本身

activation 是一个运行时对象，owner 是一个语义归属。

一个 grain 现在被哪个节点“负责”，并不等于这个节点上永远有一个活着的 activation。

更准确地说：

- owner 表示 grain 当前应该由谁来接单
- activation 表示这个 grain 当前在某个节点上的具体执行体

### 3.2 owner 是分布式目录里最重要的那一层事实

当系统是多节点时，调用方真正需要知道的，不是某个 activation 的内存地址，而是：

- 这个 grain 现在应该由哪个节点处理
- 如果这个节点不行了，下一次去哪儿重新查

所以 owner 语义是：

- 可定位
- 可失效
- 可重新计算

它不是本地对象引用，也不应该直接跟生命周期绑定死。

### 3.3 owner 和 activation 之间要留一层缓冲

这是重建时最该坚持的一点。

如果 owner 和 activation 绑死：

- activation 一销毁，owner 语义就一起没了
- 本地回收和分布式失效会混在一起
- 目录无法只做目录

正确的做法是：

- owner 语义挂在 distributed directory 上
- activation 语义挂在 local activation table 上

中间通过 routing 串起来。

---

## 4. 为什么本地 activation 表和分布式目录不能混成一层

这个问题是很多系统做脏的根。

### 4.1 它们处理的是两种完全不同的事实

本地 activation 表处理的是：

- 这个节点上有没有这个 activation
- 这个 activation 要不要创建
- 这个 activation 要不要卸载

分布式目录处理的是：

- 这个 grain 的 owner 是谁
- 这个 owner 现在还有效吗
- 这个 grain 要不要重新定位

这两个问题不在同一层。

### 4.2 它们的失效模式也不一样

本地 activation 表失效，通常只是本节点对象没了。

分布式 directory 失效，意味着：

- owner 记录可能过期
- 缓存可能不可信
- 调用可能要重新定位
- 远端节点可能已经退出

如果这两种失效混在一起，最后就会变成“任何问题都靠重建 activation 解决”，那样系统会很快失去可推理性。

### 4.3 它们的生命周期边界也不一样

本地 activation 的生命周期，通常跟 runtime、scheduler、回收策略有关。

分布式 directory 的生命周期，跟 node membership、owner lease、失效传播、重定位有关。

如果放在一个类里，最后就会出现这种坏味道：

- 路由代码在管回收
- 回收代码在改 owner
- owner 失效代码在删本地对象

这就是 Orleans 这层最容易长脏的原因。

---

## 5. locator / cache / invalidations 各自干什么

如果重建，我会把这三件事分开。

### 5.1 locator

`locator` 负责把一个 `GrainId` 缩到一个可用的目标节点语义上。

它回答的是：

- 这个 grain 现在应该先看谁
- 先查本地 cache，还是先查 owner
- 如果 cache 过期了，往哪儿重新定位

它不负责 activation 创建。

### 5.2 cache

cache 只是 directory 的加速层，不是最终事实本身。

它可以缓存：

- owner 记录
- 最近一次定位结果
- 本地可达性信息

但它必须允许失效。

如果 cache 不能失效，它就不是 cache，是一层假事实。

### 5.3 invalidations

invalidations 负责把“旧的定位结果不再可信”这件事传播出去。

它至少要能表达：

- owner 变了
- 节点下线了
- 目录记录过期了
- 本地缓存该丢了

这层最重要的不是“马上把所有缓存都刷干净”，而是“保证后续调用不会继续盲信旧地址”。

---

## 6. owner 失效后的最小恢复链路

这条链路如果写不清，多节点就会很乱。

我会把它拆成 5 步。

### 6.1 第一步：调用先命中 locator

调用方拿着 `GrainId`，先走 locator。

locator 先看 cache。

如果 cache 还可信，就返回一个 owner 方向的 `GrainAddress`。

### 6.2 第二步：routing 决定本地还是远端

routing 看到这个地址后，决定：

- 如果 owner 在本地，走本地 activation table
- 如果 owner 不在本地，走远端转发

这一步不能直接做 activation 复用。

### 6.3 第三步：远端失败后触发 invalidation

如果远端投递失败，或者对方返回“owner 不再有效”，本地就要把这条目录记录标成过期。

注意这里失效的是目录记录，不是本地 activation 对象本身。

### 6.4 第四步：重新定位

目录失效后，再次调用同一个 `GrainId`，locator 重新计算 owner。

这可能还是原节点，也可能换成新的 owner。

### 6.5 第五步：落到新的本地 activation

等 routing 最终确认本地可执行时，再去 local activation table 创建或复用 activation。

也就是说，恢复链路是：

`调用 -> locator -> routing -> invalidation -> relocalize -> local activation`

不是：

`调用失败 -> 删除 activation -> 再随便重建`

这两种完全不是一回事。

---

## 7. 重建时该怎么切

如果我要重新做一版，我会把这几层切得很明确。

### 7.1 `IGrainDirectory`

这个接口只负责 grain owner 语义。

它应该能：

- 查询 owner
- 写入 owner
- 失效 owner
- 重新定位

它不该直接管 activation 实例。

### 7.2 `IGrainLocator`

这个接口只负责把 `GrainId` 变成某种可路由目标。

它可以读 directory，也可以读 cache，但它不该自己存生命周期事实。

### 7.3 `IActivationTable`

这个接口只负责本节点 activation 的创建、复用、卸载和回收。

它和 distributed directory 不能是一个东西。

### 7.4 `IRouter`

这个接口只负责判断：

- 本地执行
- 远端转发
- 目录失效后重新查

它不要兼任 directory。

### 7.5 `IRuntime`

这个接口只负责协调一次调用的完整消息生命周期。

它不该知道本地表怎么建，也不该知道 owner lease 怎么算。

---

## 8. 最关键的判断

如果只保留一句话，我会这样写：

> 单节点里的 activation directory 是本地对象表，多节点里的 grain directory 是 owner 语义层，二者必须分开。

这句话很朴素，但它几乎决定了后面系统会不会长脏。

只要把这条守住，后面再加 membership、placement、gateway、失效传播、远端投递，边界都还有地方放。

如果这条守不住，后面所有东西都会被塞进“目录”这个看似方便、实际上很危险的黑箱里。

