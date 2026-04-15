# 生成的 Grain Implementation 元数据与 Activator 发现（第六十五篇）

## 1. 这一篇补哪块

64 把 grain 这条线推进到了：

- `contract -> grain type -> generated reference`
- 已经能从 generated metadata 自动恢复

但 activation 这一半当时还是故意留在手工层：

- `echo -> () => new EchoGrain()`
- `counter -> () => new CounterGrain()`

这说明 grain 这条主链虽然已经拆成了两张表：

1. reference binding
2. implementation activator

可其中后一张表还没进入 metadata discovery。

所以这一篇补的是：

`generated grain implementation metadata -> builder assembly scan -> grainType -> activator factory`

## 2. 为什么 activator 这层也不能一直手工写

只要 grain reference 已经自动发现，而 activator 还靠手工白名单，系统就会停在一个很尴尬的阶段：

- `GetGrain<T>()` 看起来已经像个 metadata-driven runtime
- 真到创建 activation 时，却还得回头问应用入口“你有没有手工注册实现工厂”

这会让运行时边界继续裂成两半。

更重要的是，完整复刻 Orleans 时，activation 这层迟早也要回答几个问题：

1. 这个 `grainType` 对应哪个实现类型
2. 要怎么创建实例
3. 后面如果接 DI / 生命周期 / placement 属性，该往哪儿挂

如果这层还一直保留在手工 `AddGrainImplementation(...)`，后面再长 manifest 时，activator 这半就还得再推倒重来一次。

## 3. 这一版具体做了什么

当前这版先补了一个很窄的 activator metadata 版本：

1. 新增 `GeneratedGrainImplementationAttribute`
   - 只声明一件事：
     - `GrainType`
2. `EchoGrain` / `CounterGrain`
   - 改成 `partial`
3. `Demo/Generated/*.g.cs`
   - 生成 partial metadata 壳：
     - `[GeneratedGrainImplementation("echo")]`
     - `[GeneratedGrainImplementation("counter")]`
4. `OrleansReplicaKernelBuilder.AddGeneratedGrainImplementationsFromAssembly(...)`
   - 宿主把程序集交给 builder 扫描
5. builder 在 `Build(...)` 前统一收集 activator
   - 验证实现类型
   - 找 parameterless ctor
   - 生成 `grainType -> Func<object>` 工厂
6. `LocalActivationDirectory`
   - 继续只消费 `grainType -> grainFactory` 这张表

这样一来，当前 demo 和测试就不再需要手工写：

- `.AddGrainImplementation("echo", ...)`
- `.AddGrainImplementation("counter", ...)`

## 4. 为什么故意只做到 parameterless ctor

这一版没有急着把 activator 做成一个“大而全”的系统，而是先只支持：

- public parameterless constructor

这是刻意的。

因为现在真正最需要验证的是：

`generated metadata 能不能把 activator 这条链收进 builder`

而不是一上来就把下面这些全搅进来：

- DI
- service provider
- grain constructor injection
- 生命周期挂钩
- placement / reentrancy / collection age 属性合流

这些能力后面都要做，但如果现在一起上，反而会把“activator metadata discovery 这件事本身”淹掉。

## 5. 这一轮最重要的设计点

这次其实是在把 grain 这条线补成一个更完整的四段式：

1. generated grain reference metadata
2. generated grain implementation metadata
3. builder assembly scan
4. runtime registration table

也就是说，当前 grain 这条主链终于不再是：

- 前半段靠 generated metadata
- 后半段靠手工注册

而是开始变成同一种结构。

这件事的意义，比“少写两行 builder 代码”大得多。

因为它说明：

`generated code 已经能把 grain 这条链两头最核心的事实都带回运行时。`

## 6. 这和完整 grain manifest 还差多远

差得还很远。

这一版还没做的包括：

1. grain implementation 的 DI activator
2. 生命周期 / placement / reentrancy / collection 配置 metadata
3. grain properties 的统一 manifest
4. reference / implementation / invokable / serializer 的统一 provider
5. application part 风格的跨程序集聚合

所以这次不能说“grain manifest 做完了”，更准确地说是：

`grain activation 这一半终于也进入了 generated metadata + builder discovery 的轨道。`

## 7. 为什么这一步对后面很关键

如果 activator 这层不先走到这里，后面很多东西都不好继续长：

- DI activator 不知道该挂在哪层
- grain class 属性不知道该跟谁汇总
- manifest provider 不知道该先吃哪份事实
- application part 也只会继续停在半自动状态

而现在 grain 这条线至少已经具备了一个很清楚的骨架：

- contract binding 自动发现
- implementation activator 自动发现

后面再往：

- invokable metadata
- serializer metadata
- grain properties metadata

推进时，就不必每一层都重新发明一套注册办法。

## 8. 当前阶段最准确的描述

如果用一句话概括这一轮，最准确的说法是：

`OrleansReplicaKernel 已经不再手工维护 demo 里这批 grainType 到实现工厂的映射，而是开始从 generated grain implementation metadata 自动恢复 activation 所需的 activator；但这还只是 activator discovery，不是完整 grain manifest。`
