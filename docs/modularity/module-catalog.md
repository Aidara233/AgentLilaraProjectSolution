# 模块目录：唯一性、类型图、实例图

> 承接 `module-contract.md`。未实现、未编译。
> 两个新增命题：**① 图本身就是一个模块**；**② 多实例模块同时拥有"类型图"和"属于自己那个实例的图"**。

---

## 0. 唯一性是一等属性，而且必须二选一

每个模块都必须明确回答「是否恰好唯一」，不允许"看情况"。判定规则只有一条：

> **唯一性由「是否持有私有状态」决定；"用不常用"由参数决定，不能用多实例去表达。**

| 情况 | 结论 | 理由 |
|---|---|---|
| 有私有状态，且状态按某主键天然分区 | **多实例**，实例键 = 那个主键 | 分区是领域事实，不是优化 |
| 有私有状态，但全局只有一份 | **唯一（Singleton）** | 两份就不一致 |
| **无私有状态** | **唯一 + 参数化** | 为对称强行多实例只会增加寻址成本 |

第三条是常见错误。上一版 `module-contract.md` 里我把 `channel.context` 写成 per-channel，是错的：它无状态（`ChannelContextService` 的构造只注入 `IChannelDataSource`，`Evaluate` 靠入参），**应该是唯一实例 + `channelKey` 参数**。

---

## 1. 三个层级，同一个契约形状

| 层级 | 是什么 | 对外契约 | 内部 |
|---|---|---|---|
| **叶子模块** | 一个类实现一个 `[Module]` 接口 | 行为 + 数据 + 生命周期事件 | 无 |
| **复合模块** | 一张**类型图** + 每实例一张**实例图** | 同上（只暴露公开端口子集） | 节点 + 连线 |
| **根网络** | 整张连接网络本身，也是最外层的复合模块 | 同上 | 全部节点 |

三者的契约形状完全相同 —— 这就是"会话实例也是个模块"能成立的原因：**从外面看，`session.actor#42` 与 `vision` 没有区别**，都是一组行为、一组数据、一串生命周期事件。

---

## 2. 多实例模块 = 类型图 + 实例图

### 2.1 两张图分别是什么

| | 类型图（Class Graph） | 实例图（Instance Graph） |
|---|---|---|
| 身份 | `GraphClass.ClassId`（已有） | `GraphInstance.InstanceKey`（已有） |
| 内容 | 节点声明 + 连线 + 变量声明 | 变量值 + latch + 本实例独有的节点/连线 |
| 数量 | 每种模块类型**恰好一张** | 每个实例**恰好一张** |
| 归属 | 进程级，只读 | 实例级，随实例生灭 |
| 生命周期 | `Loaded` → `Validated` → `Sealed`（→ 可选 `Reloaded`） | `Created` → `Mounted` → `Active`/拍 → `Snapshot` → `Killed` |
| 持久化 | 不进快照（是代码/资源的一部分） | 进快照（`Snapshot` / `Restore`） |

### 2.2 合成规则

```
有效图 = 类型图  ⊕  实例图 overlay
```

- **允许**：加节点、加连线、覆盖变量初值
- **禁止**：删除类型图的节点或连线（删了它就不再是那个类型）
  - 需要"关掉某条"时用 `disabled: true`（软禁用），保留结构以便诊断与回滚
- **冲突**：实例图同名节点覆盖类型图节点，且必须过类型校验（端口签名一致）

### 2.3 为什么这个模型顺手解决了三个卡点

| 卡点 | 为什么它不再是问题 |
|---|---|
| **M1 多实例绑定** | 不必把全局 `ModuleHost._implementations` 改成二元组键。**绑定发生在实例图合成时**：类型图里只有节点声明，实例图合成时把每个节点绑到"这个实例自己的模块实现"。类型图越干净，实例之间越不会串。 |
| **M6 复合模块** | `Composite = true` 不再需要新机制 —— 复合模块就是"有类型图 + 有实例图的模块"。 |
| **Q14 多实例下读哪个 property** | `ModulePropertyReadNode` 活在实例图里，`_implementation` 在合成时已确定为该实例的。现有代码"构造时抓单个实现"（`ModuleNodes.cs:227,240,256`）在实例图模型下**恰好是对的**。 |

### 2.4 一个直接推论

**类型图是唯一被共享的可变风险点。** 所以类型图必须 `Sealed`：装载后不可改。要改就整张 `Reloaded`，并把所有实例图重新校验一遍 —— 否则会出现"一半实例按旧结构、一半按新结构"的半死状态。

---

## 3. 图作为模块：`graph`

最外层那张连接网络确实是个模块，而且它的行为和别的模块没有区别。

### 3.1 两个东西要分开

| | `graph`（模块类型） | `graph.root`（内建唯一实例） |
|---|---|---|
| 是什么 | 任意一张图 | **整个框架的连接网络** |
| 唯一性 | **不唯一**（按 `ClassId` 一类型一实例图） | **唯一**（一进程一张） |
| 例子 | `builtin.ingress`、用户自定义图 | 装配根网络 |

### 3.2 `graph` 暴露什么

| 维度 | 内容 |
|---|---|
| **行为** | `dispatch(InboundEvent)` → `SolveResult`（一拍）<br>`validate()` → `ValidationReport`<br>`reset()`<br>`snapshot()` / `restore(blob)`<br>`swapClass(newClassId)` 热换（见 §2.4 限制）<br>`inspect()` → 结构 + 上次求解诊断 |
| **数据** | `variables`（只读快照）<br>`lastIntents`（只读）<br>`lastResult`（只读）<br>`nodeCount` / `linkCount`（只读）<br>`classId`（只读）<br>`instanceKey`（只读） |
| **生命周期事件** | 类型图侧：`Loaded` → `Validated` → `Sealed`（→ `Reloaded`）<br>实例侧：`Created` → `Mounted` → `Active`（每拍，高频）/ `Deactivated` → `Snapshot` → `Killed`<br>`Crashed(reason)`：求解异常 |

注意 `dispatch` 和现有 `GraphInstance.Run(EvalContext)` 是同一件事，只是**对外收成一个 InboundEvent、返回一个结果**，不再把 `EvalContext` 泄出去。这一步是把"图"从库变成模块的关键——现在 `Run` 的入参是求解上下文，外人得自己搭。

### 3.3 为什么值得把它"称呼"为模块

三个实际好处，不是命名洁癖：

1. **它有了生命周期**：图的装载/校验/封存/热换现在是散落在 `GraphLoader` + `ModuleHost` + `MasterEngine` 里的过程；作为模块，它有明确的事件序列，且 `Sealed` 之后拒绝修改（这是 §2.4 需要的不变量）。
2. **它可被观察**：`inspect()` 暴露结构与上次求解诊断，`variables` / `lastIntents` 是只读数据口 —— webui 独立进程后，这就是"网络图"页面的数据源（对应 `modules.json` 里 webui 的 `api` 边）。
3. **它可被替换**：`swapClass` 让整张网络可热换，这是"内置连接与自定义网络同一方案"的最终形态——换的不是一条连接，而是整张图。

---

## 4. 逐模块目录

「唯一」= 恰好一个实例；「多实例」= 实例键见列。**没有第三种。**

### 4.1 根与基础设施

| 模块 | 唯一性 | 实例键 | 行为 | 数据 | 生命周期事件 |
|---|---|---|---|---|---|
| `graph.root` | **唯一** | — | `dispatch` / `validate` / `snapshot` / `inspect` / `swapClass` | `variables`、`lastIntents`、`nodeCount`、`linkCount` | `Loaded`→`Validated`→`Sealed`；实例侧 `Mounted`/`Active`/`Snapshot`/`Killed` |
| `host` | **唯一** | — | `boot` / `shutdown` / `rebind` | `config`、`uptime`、`moduleInventory` | `Created`→`Mounted`→`Ready`；`ShutdownRequested`（**可拒**：有活跃会话时）→`Killing`→`Killed` |
| `contracts` | — | — | 无 | 纯类型 | 无 |
| `data` | **唯一** | — | 不暴露为图节点，只提供 `I*Repository` 实现 | — | `Mounted`（建连接、跑迁移） |
| `llm` | **唯一** | — | 只提供 `ILlmPort` 实现 | `inFlight`（只读，诊断用） | `Mounted`（加载 provider 配置） |
| `logging` | **唯一** | — | 只提供 `ISignalLogger` 实现 | — | `Mounted`；`Killed`（flush） |

### 4.2 连接与工具

| 模块 | 唯一性 | 实例键 | 行为 | 数据 | 生命周期事件 |
|---|---|---|---|---|---|
| `adapters.*` | 多实例 | `adapterId` | `send` / `probe` / `own(channelKey)` | `connected`、`platform`、`owns` | `Created`→`Mounted`；`Disabled`（掉线不注销）/`Enabled`；`Crashed(reason)`（可重连）；`Killed` |
| `plugin-host` | **唯一** | — | `load` / `unload` / `reload` / `invoke(tool, args)` | `loadedPlugins`、`toolRegistry` | `Mounted`（扫目录）；`Reloaded`；`Killed`（全卸载 + 注册表反注册） |
| `plugin`（每个插件） | 多实例 | `pluginId` | 暴露其 `ITool` 行为（工具是行为，不是实例） | `enabled`、`toolNames` | `Loaded`→`Mounted`；`Enabled`/`Disabled`；`Reloaded`；`Killed` |
| `commands` | **唯一** | — | `handle(text)` / `list()` | `registry`（只读） | `Mounted` |
| `webui` | 独立进程 | — | 不暴露为图节点，只经 API | — | 随进程 |

### 4.3 频道域

| 模块 | 唯一性 | 实例键 | 行为 | 数据 | 生命周期事件 |
|---|---|---|---|---|---|
| `channel.occupancy` | **唯一** | — | `acquire(channelKey, sessionId, level)`→`Grant/Deny`<br>`release(channelKey, sessionId)`<br>`query(channelKey)`<br>`reap(olderThan)` | `table`（只读快照）、`version` | `Mounted`（从 `IChannelStore` 重建）；`Slept`（暂停 reaper）；`ShutdownRequested`（**可拒**：仍有活跃占用） |
| `channel.state` | 多实例 | `channelKey` | `patch(StatePatch)` / `reset()` | `affinity`（读写）、`memo`（读写）、`config`（读写）、`lastActiveAt`（只读） | `Created`（频道首次出现）→`Mounted`（从 store 加载）；`Snapshot`/`Restore`；`Killed`（归档） |
| `channel.context` | **唯一（无状态）** | — | `build(channelKey, limit)` → `ChannelContextFragment` | — | 无（纯查询；不需要生命周期） |
| `channel.route` | **唯一** | — | `decide(InboundEvent, OccupancySnapshot)` → `RouteDecision` | `rules`（读写） | `Mounted`（加载内置规则）；`Reloaded`（规则热更） |

`channel.context` 从 per-channel 改回唯一实例，是 §0 那条规则的直接应用：**它没有私有状态**。

### 4.4 会话域

| 模块 | 唯一性 | 实例键 | 行为 | 数据 | 生命周期事件 |
|---|---|---|---|---|---|
| `session.actor` | 多实例（**复合**） | `sessionId` | `route` / `prompt` / `inject` / `requestExit` / `snapshot` / `restore` | `status`、`sessionType`、`parentId`、`roundCount`、`contextUsage` | 全套；`ShutdownRequested`**可拒**（长任务不被杀）；`Restored` 跳过 `BuildStartPromptSection`；`Crashed` 留现场 |
| `turn` | 多实例 | `sessionId` | `run(turnInput)`→`TurnResult` / `abort(turnId)` | `turnId`、`phase`（组上下文/调模型/执行工具/落库） | `Activated`/`Deactivated`（**每拍**，唯一高频）；`Snapshot`（仅轮边界） |
| `context` | 多实例 | `sessionId` | `assemble(fragment, history)` / `compress(tier)` / `inject(text, weight)` | `usage`、`compressionTier`、`promptContributions` | `Mounted`；`Snapshot`/`Restore`；`Slept`（清 L1） |
| `mind` | 多实例 | `sessionId` | `observe(event)` / `recall(query)` / `enqueueReview(item)` | `emotionSnapshot`、`heatCapacity` | `Mounted`；`Snapshot`/`Restore`；`Slept`（衰减暂停） |
| `vision` | **唯一** | — | `analyze(imageRef)` / `ocr(imageRef, threshold)` | `phaseConfig`（读写）、`queueDepth` | `Mounted`；`Paused`/`Resumed`（省资源）；`ShutdownRequested`（队列非空时可拒/要求排空） |
| `recurring` | **唯一** | — | `schedule(task)` / `cancel(id)` / `tick(now)` | `pendingCount`、`nextFireAt` | `Mounted`（从库恢复）；`Slept`（全挂起，**不可拒**）；`Killed`（落库） |

`vision` 判为唯一：它的 `phaseConfig` 是全局配置，不是每张图一份；无状态流水线靠入参区分。

### 4.5 应用

| 模块 | 唯一性 | 说明 |
|---|---|---|
| `console-client` | 独立进程 | 只消费 contracts 与 API |
| `graph-sandbox` | 测试夹具 | 不进生产网络 |

---

## 5. 可以写进加载期断言的规则

这些都能自动检查，不需要人肉评审：

| # | 断言 | 依据 |
|---|---|---|
| U1 | 每个模块声明必须给出 `Scope`，且 `Scope ∈ {Singleton, PerSession, PerChannel, PerAdapter, PerGraphClass}` | §0 |
| U2 | `Scope = Singleton` 的模块，实例键字段必须不存在（防止"既唯一又能寻址"的暧昧态） | §0 |
| U3 | 声明多实例的模块，必须在类型图里有至少一个 `per-key` 变量 | 否则它其实无状态，应降为唯一 |
| U4 | 实例图的节点/连线集合 ⊇ 类型图的节点/连线集合（不允许删除） | §2.2 |
| U5 | 类型图必须 `Sealed` 之后才允许创建实例 | §2.4 |
| U6 | `instance: "*"` 只能接只读数据口 | `module-contract.md` §2 |
| U7 | 复合模块对外暴露的端口 ⊆ 内部图的公开端口 | 内部图私有 |
| U8 | `graph.root` 每进程恰好一个；其余 `graph` 实例的 `InstanceKey` 必须唯一 | §3.1 |

---

## 6. 未验证

| id | 问题 |
|---|---|
| Q17 | 类型图与实例图的合成是否要支持"实例图引用另一个类型图"（嵌套复合模块）。会话已经嵌套一层（session → turn/context/mind/vision），再往下还有吗？ |
| Q18 | 实例图 overlay 的持久化格式：现有 `SessionPersistence` 存的是会话状态 blob，不是"节点/连线增量"。要扩格式还是另存？ |
| Q19 | `GraphClass` 现在是 C# 对象（`IReadOnlyList<INode>`），`ClassId` 只是字符串。**"类型图恰好一张"这个不变量现在没有任何东西在守护** —— 需要注册表 + 重复 `ClassId` 检测。 |
| Q20 | `swapClass` 热换时，正在拍内的实例怎么办（等在拍结束还是打断）？ |
| Q21 | `host` 的 `ShutdownRequested` 可拒（有活跃会话时）—— 但现有 `Program.cs` 是同步 `Main`，优雅退出的路径是否已存在？未读。 |

**未验证声明**：本文仍基于静态阅读，未 build / 未 run / 未 test。§4 表里的行为名与数据名是**按职责推断**，不全是现有真实签名；`channel.*`、`mind` 的行为目前散在 `ChannelContextService` / `ChannelAccessImpl` / `MindEngine` 里，没有以模块方法形式存在。
