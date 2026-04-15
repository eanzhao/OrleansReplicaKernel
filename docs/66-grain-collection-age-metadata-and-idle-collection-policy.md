# Grain 回收时间元数据与空闲回收策略（第六十六篇）

## 1. 这一篇补哪块

65 把 grain activation 这一半推进到了：

- generated grain implementation metadata
- builder assembly scan
- `grainType -> activator factory`

但那一版本质上还是“装配层自动化”，还没真正让 grain class metadata 去影响 runtime 行为。

这一篇往前再推半步，补的是第一类真正落进运行时语义的 grain 元数据：

`per-grain collection age`

也就是：

- 同一个 runtime 里
- 不同 grain type
- 可以有不同的 idle collection 门槛

## 2. 为什么先补 collection age，而不是先补 reentrancy / placement

这一步是刻意选过的。

如果现在就直接去补：

- reentrancy
- interleaving
- placement attribute
- migration preference

这些东西会立刻碰到更大的一坨：

- scheduler 语义
- message ordering
- placement policy resolver
- grain property 合流

那样会把“grain class metadata 终于开始影响 runtime 行为”这件事本身淹掉。

`collection age` 不一样。

它有几个很适合当前阶段的优点：

1. 足够真实，Orleans 本来就有这条线
2. 足够独立，不用先改半个 scheduler
3. 足够能验证，demo 和测试都很容易看出来
4. 足够能往后长，后面可以自然接到完整 grain manifest

所以这次故意先从 idle collection 下手。

## 3. 这一版具体做了什么

当前这版先补了一个很窄但很有代表性的版本：

1. `GeneratedGrainImplementationAttribute`
   - 新增 `CollectionAgeLimitMilliseconds`
2. `EchoGrain` 的 generated activator metadata
   - 现在声明了更长的 collection age
3. `CounterGrain` 的 generated activator metadata
   - 现在声明了更短的 collection age
4. builder 扫描 generated grain implementation 时
   - 会把这份 collection age 一起收进 grain implementation registration
5. `LocalActivationDirectory`
   - `CollectIdleAsync(defaultIdleFor)` 不再只认一个全局窗口
   - 会先按 `grainType` 找有没有 class-specific collection age
   - 有就优先用 grain 自己的
   - 没有才回退到调用方传进来的默认窗口

这样当前 runtime 里终于出现了一层真正的优先级：

- grain-specific collection age
- default idle collection window

## 4. 这层优先级为什么重要

前面几篇一直在强调一个方向：

`不要让运行时只剩“全局一个大开关”，而要让 grain type 自己也能带回局部事实。`

collection age 正是这个方向里最早、最直观的一层。

如果 runtime 只有：

- `CollectIdleGrainsAsync(TimeSpan idleFor)`

那就意味着所有 grain 最后都只能吃同一个 idle 口径。

这当然能跑，但很快就会遇到两个问题：

1. 业务上不同 grain 的活跃模式根本不一样
2. 后面就没法自然接上 Orleans 那种：
   - manifest
   - attribute
   - class-specific option
   - global fallback

这一整套优先级链

所以这次虽然还只是一个很小的版本，但方向已经对了：

`全局默认值不再是唯一 truth。`

## 5. 当前实现故意收得很窄

这次不是在完整复刻 Orleans 的回收配置体系。

故意还没做的包括：

1. `[CollectionAgeLimit]` 用户属性
2. `[KeepAlive]` / collection exemption
3. class-specific option 配置源
4. grain manifest 里的完整 lifecycle property merge
5. activation collector 的时间轮 / working set / quantum

也就是说，现在还没有做到 Orleans 那种完整优先级：

- manifest
- attribute
- class-specific config
- global config

当前只做到：

- generated implementation metadata
- global fallback

但这已经足够让 grain class metadata 真正影响运行时行为了。

## 6. 为什么这比“只是多了一个配置项”更重要

表面上看，这轮像是只是给 generated metadata 多塞了一个：

- `CollectionAgeLimitMilliseconds`

但真正重要的是：

`grain implementation metadata 已经不再只负责“怎么 new”，而开始负责“这个 grain type 在 runtime 里应该怎么活”。`

这是个很关键的拐点。

因为从这里开始，grain class metadata 就不再只是装配信息，而开始进入运行时语义。

后面再往下长：

- placement hint
- reentrancy flag
- migration policy
- lifecycle policy

逻辑上就顺了很多。

## 7. 这一版怎么验证最有价值

当前版本最值得看的验证，不是“字段有没有扫出来”，而是：

### 7.1 default idle window 比 grain 自己的 age 更大

如果：

- 默认 idle 窗口是 `300ms`
- `counter` 的 grain-specific age 是 `100ms`
- `echo` 的 grain-specific age 是 `5000ms`

那在同样等待 `150ms` 之后：

- `counter` 应该已经能被收掉
- `echo` 不应该被收掉

### 7.2 收掉之后的行为差异必须真能看出来

这一步不能只看内部日志，最好直接看业务语义：

- `counter` 被收掉后再调，状态应该从头开始
- `echo` 没被收掉，再调应该延续旧状态

这才说明 collection age metadata 已经真的进入 activation 生命周期，而不是停在某张注册表里没被消费。

## 8. 对完整复刻 Orleans 的意义

如果把 64、65、66 三篇连起来看，会发现 grain 这条线已经开始形成层次了：

1. generated grain reference metadata
2. generated grain implementation metadata
3. generated grain lifecycle/collection metadata

虽然这三层都还只是局部版本，但已经出现了一个很重要的趋势：

`generated code 带回来的不再只是“类型在哪儿”，而是“这个 grain type 在运行时里应该怎么被理解”。`

这正是后面继续长完整 grain manifest 的基础。

不然的话，runtime 最后还是会退回成：

- 一部分事实在生成代码里
- 一部分事实在 builder 手工 glue code 里
- 一部分事实又在运行时硬编码里

那就很难说是“完整复刻 Orleans”，最多只是把几个主链拼起来了。

## 9. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的说法是：

`OrleansReplicaKernel 已经让 generated grain implementation metadata 开始影响 activation 生命周期本身：不同 grain type 现在可以带不同的 idle collection age；但这还只是 grain lifecycle metadata 的第一层，不是完整的 grain manifest merge。`
