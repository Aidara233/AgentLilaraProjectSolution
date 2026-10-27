# 模块契约：行为 / 数据 / 生命周期事件 / 实例

> 承接 `link-design.md`。未实现、未编译。
> 核心命题：**模块 = 类型 + 实例 + 行为 + 数据 + 生命周期事件**。会话实例是一个模块实例；频道、适配器、图同理。

---

## 0. 四个维度，缺一不可

| 维度 | 是什么 | 现有载体 | 现状 |
|---|---|---|---|
| **类型** | 一段自成体系的代码，只暴露接口 | `[Module("channel.occupancy")]` on interface | 已有（`Modules/ModuleAttributes.cs`） |
| **实例** | 类型的运行态，有私有状态与业务主键 | **缺** | `ModuleHost._implementations` 是 `Dictionary<string, object>`，**一模块只能一个实例** |
| **行为** | 可被外部触发的操作 | `[Method]` → `module.<id>.<method>` 节点 | 已有 |
| **数据** | 可读/可写的属性 | `[Property]` → `read.*` / `write.*` 节点 | 已有（有 setter 才生成写节点） |
| **生命周期事件** | 实例的出生 / 活跃 / 休止 / 死亡 | `[Lifecycle("mount"|"kill")]` + SDK 的 `ILoopComponent` 钩子 | **两套并存，且只有 mount/kill 两个** |

**结论：类型、行为、数据三样已经有了；缺的是实例，以及一个统一的生命周期事件集。**

---

## 1. 复合模块：会话实例就是一张图

这是"每个会话实例也是个模块"的直接落法。

```
模块 = 叶子模块（一个类）           ← 最细粒度
     | 复合模块（一张图 + 一组实例）  ← 对外同样是「行为 + 数据 + 生命周期事件」

session.actor#<sessionId>            ← 从外面看，它就是一个模块实例
  ├── turn#<sessionId>
  ├── context#<sessionId>
  ├── mind#<sessionId>
  └── vision#singleton
```

要点：

- 复合模块对外只暴露自己声明的端口，**内部那张图是私有的**（否则"会话"又会退化成一组可被随意穿透的对象，就是现在的样子）。
- `GraphInstance.InstanceKey` 已经是这个位置（`Graph/GraphInstance.cs`，默认 `{ClassId}@default`）。**把 `InstanceKey` 定为业务主键**，会话、频道、适配器共用同一套寻址。
- 于是层级是：**图实例（运行单元）= 一组模块实例（装配单元）**。一个会话 = 一个图实例 = 它的模块实例集合。

---

## 2. 实例 id 就是业务主键

| 模块类型 | 实例 id | 为什么 |
|---|---|---|
| `session.actor` | `sessionId` | 已有（`LoopInfo.SessionId`、`CreateSessionSpec.ParentSessionId`） |
| `channel.state` | `channelKey` | 频道是长期实体，天然有主键 |
| `adapter.*` | `adapterId` | 已有（`InboundEvent.AdapterId`） |
| `graph.*` | `instanceKey` | 已有 |
| `channel.occupancy` | **无（Singleton）** | 见 §5 |
| `vision` / `recurring` / `plugin-host` | **无（Singleton）** | 无状态或全局唯一 |

**机器人内部状态必须能按主键寻址**——这正好也是 webui 独立进程后的 API 路径自然形状（`GET /sessions/{id}`、`GET /channels/{key}`）。

### 节点怎么引用一个实例

```jsonc
{ "id": "ses", "type": "module.session.actor.run",
  "instance": "$beat.session" }        // 从本拍上下文解析（本拍属于哪个会话）
{ "id": "ch",  "type": "module.channel.state.read.affinity",
  "instance": "$beat.channel" }        // 本拍属于哪个频道
{ "id": "occ", "type": "module.channel.occupancy.acquire",
  "instance": "singleton" }
{ "id": "all", "type": "module.channel.state.read.affinity",
  "instance": "*" }                    // fan-out：对所有实例求值（聚合查询）
```

四种写法：`literal` / `$beat.<scope>` / `singleton` / `*`(fan-out)。`*` 只允许出现在**只读**数据口上——写和调用不能 fan-out，否则语义是"对 N 个实例做同一件事"，那是 N 条连接该干的事，不是一条连接。

---

## 3. 生命周期事件集

### 3.1 统一的一套

| 组 | 事件 | 语义 | 可否拒绝 | 现有对应 |
|---|---|---|---|---|
| **出生** | `Created` | 实例已构造，未就绪 | — | `SessionStatus.Creating` |
| | `Mounted` | 依赖已注入、端口已接线 | 否 | `[Lifecycle("mount")]`、`ILoopComponent.OnInitAsync` |
| | `Restored` | 从快照恢复完成 | 否 | `SessionStatus.Restored`、`SessionPersistence` |
| | `Ready` | 可接第一拍 | 否 | — |
| **活跃** | `Activated` | 本拍被激活 | 否 | `ILoopComponent.OnActivatedAsync` |
| | `Deactivated` | 本拍结束 | 否 | `OnAfterInvokeAsync`（近似） |
| **休止** | `Paused` / `Resumed` | 暂停但保状态 | 否 | `OnPauseAsync` |
| | `Slept(state)` / `Woke` | 系统级睡眠 | 否 | `SleepState {None, Daydream, Nap, DeepSleep}` |
| | `Enabled` / `Disabled` | 功能开关 | 否 | `OnEnabledAsync` / `OnDisabledAsync` |
| **死亡** | `ShutdownRequested(reason)` | **可拒绝**，理由回给发起方 | **是** | `OnShutdownRequestedAsync → ShutdownResponse`、`OnCanExitAsync` |
| | `Killing` / `Killed` | 正在销毁 / 已销毁 | 否 | `[Lifecycle("kill")]`、`SessionStatus.Completed` |
| | `Crashed(reason)` | 异常终止 | 否 | `SessionStatus.Crashed` |
| **持久化** | `Snapshot` / `Restore` | 状态序列化 | 否 | `SessionPersistence` |

### 3.2 可拒绝只有一处，而且必须保留

现有代码里有两处先例：`OnShutdownRequestedAsync` 返回 `ShutdownResponse.Allow=false`，`OnCanExitAsync` 返回 `false` 阻止 `end_session`。

这是**唯一允许"模块对框架说不"的入口**，一定要保住——否则长任务会被外部随意杀掉。规则：

- 只有 `ShutdownRequested` 可拒；
- 拒绝必须附理由，且**发起方收到理由**（不是静默失败）；
- 框架可设强制上限（超时后 `Killing`），但那是显式策略，不是默认。

### 3.3 生命周期事件走"带外"，不占一拍

这是与 `link-design.md` 的分工，也顺手回答了那里的 Q8：

| 通信 | 载体 | 同步性 | 可求解 | 可拒绝 |
|---|---|---|---|---|
| **拍内数据流** | `Link` | 同步，一拍一次求解 | 是 | 否（失败整拍零副作用） |
| **带外事件** | 订阅分发（现 `EventBus` / `PublishGlobal`） | 异步 | 否 | 仅 `ShutdownRequested` |

**生命周期事件绝不能占一拍**：一拍 = 一个 `InboundEvent` → 一次 `Solve`，如果把 `Paused` 也塞进拍里，求解语义立刻崩。

但生命周期事件**必须对图可观察**：框架调用 `[Lifecycle]` 方法，**同时**发一条带外事件，图可以订阅它（"会话被暂停了 → 清一下缓冲"）。可观察、不可干预——这条界线要划死。

---

## 4. 每个模块该暴露什么

只列**功能主体**。`contracts` 无行为（纯类型）；`host` 是组合根（只有装配行为）；`data` / `llm` / `logging` 是基础设施（行为写在端口上，不是模块）。

### 4.1 channel 域

| 模块 | 实例粒度 | 行为（方法） | 数据（属性） | 生命周期事件 |
|---|---|---|---|---|
| `channel.occupancy` | **Singleton** | `acquire(channelKey, sessionId, level)` → `Grant/Deny`<br>`release(channelKey, sessionId)`<br>`query(channelKey)` → `OccupancySnapshot`<br>`reap(olderThan)` 清理僵尸占用 | `occupancyTable`（只读快照）<br>`version`（用于判断是否变更） | `Mounted`（从 DB 重建占用表）<br>`Slept`（暂停 reaper）<br>`ShutdownRequested`（**可拒**：还有活跃占用时拒绝） |
| `channel.state` | **PerChannel** | `patch(StatePatch)` 合并写入<br>`reset()` | `affinity: float`（读/写）<br>`memo: string?`（读/写）<br>`config: ChannelConfig`（读/写）<br>`lastActiveAt`（只读） | `Created`（频道首次出现）<br>`Mounted`（从 store 加载）<br>`Snapshot` / `Restore`<br>`Killed`（频道下线，归档） |
| `channel.context` | PerChannel（无状态，可 Singleton + 参数） | `build(channelKey, limit)` → `ChannelContextFragment` | `lastFragmentAt`（只读，诊断用） | 无（纯查询，不需要生命周期） |
| `channel.route` | Singleton | `decide(InboundEvent, OccupancySnapshot)` → `RouteDecision` | `rules`（读/写） | `Mounted`（加载内置规则）<br>`Reloaded`（规则热更新） |

**注意 `channel.occupancy` 是 Singleton 而非 PerChannel** —— 见 §5，这是对 `link-design.md` 的一处修正。

### 4.2 session 域

| 模块 | 实例粒度 | 行为 | 数据 | 生命周期事件 |
|---|---|---|---|---|
| `session.actor` | **PerSession**（复合模块） | `route(RouteDecision)`<br>`prompt(text)`<br>`inject(text)`<br>`requestExit()`<br>`snapshot()` / `restore(blob)` | `status: SessionStatus`（只读）<br>`sessionType: string`（只读）<br>`parentId`（只读）<br>`roundCount: int`（只读）<br>`contextUsage`（只读） | 全套。特别地：<br>`ShutdownRequested` **可拒**（长任务不被杀）<br>`Restored` 时跳过 `BuildStartPromptSection`（现有语义）<br>`Crashed` 保留现场供恢复 |
| `turn` | PerSession | `run(turnInput)` → `TurnResult`<br>`abort(turnId)` | `turnId`（只读）<br>`phase`（只读：组上下文/调模型/执行工具/落库） | `Activated` / `Deactivated`（**每一轮一次**，这是唯一高频生命周期事件）<br>`Snapshot`（半轮不持久化，只在轮边界） |
| `context` | PerSession | `assemble(fragment, history)`<br>`compress(tier)`<br>`inject(text, weight)` | `usage`（只读）<br>`compressionTier: L1/L2/L3`（只读）<br>`promptContributions`（只读） | `Mounted`（加载压缩配置）<br>`Snapshot` / `Restore`<br>`Slept`（清 L1 提示） |
| `recurring` | **Singleton** | `schedule(task)`<br>`cancel(taskId)`<br>`tick(now)` | `pendingCount`（只读）<br>`nextFireAt`（只读） | `Mounted`（从库恢复定时任务）<br>`Slept`（全部挂起，**不可拒**）<br>`Killed`（落库） |

### 4.3 认知与感知

| 模块 | 实例粒度 | 行为 | 数据 | 生命周期事件 |
|---|---|---|---|---|
| `mind` | PerSession | `observe(event)`<br>`recall(query)`<br>`enqueueReview(item)` | `emotionSnapshot`（只读）<br>`heatCapacity`（只读配置） | `Mounted`<br>`Snapshot` / `Restore`<br>`Slept`（情绪衰减暂停） |
| `vision` | **Singleton** | `analyze(imageRef)` → `VisionResult`<br>`ocr(imageRef, richTextThreshold)` | `phaseConfig`（读/写）<br>`queueDepth`（只读） | `Mounted`（加载 phase 配置）<br>`Paused` / `Resumed`（省资源）<br>`ShutdownRequested`（队列非空时**可拒**或要求排空） |

### 4.4 连接与工具

| 模块 | 实例粒度 | 行为 | 数据 | 生命周期事件 |
|---|---|---|---|---|
| `adapters.*` | **PerAdapter** | `send(channelKey, content)`<br>`probe()` | `connected: bool`（只读）<br>`platform`（只读）<br>`owns`（读/写：频道匹配规则） | `Created`（注册）<br>`Mounted`（连上）<br>`Disabled`（掉线但不注销）<br>`Crashed(reason)`（协议异常，可重连）<br>`Killed` |
| `plugin-host` | Singleton | `load(pluginDir)`<br>`unload(pluginId)`<br>`reload(pluginId)`<br>`invoke(toolName, args)` | `loadedPlugins`（只读）<br>`toolRegistry`（只读） | `Mounted`（扫描目录）<br>`Reloaded`（热更新）<br>`Killed`（全卸载 + 注册表反注册） |
| `graph` | PerGraphClass（实例 = `GraphInstance`） | `run(InboundEvent)`<br>`reset()` | `variables`（只读快照）<br>`lastIntents`（只读） | `Mounted`（绑定实现，未绑即失败）<br>`Snapshot` / `Restore`（变量）<br>`Killed` |

### 4.5 基础设施（行为在端口上，不是模块）

| 模块 | 实例粒度 | 说明 |
|---|---|---|
| `data` | Singleton | 只提供 `I*Repository` 实现；**不暴露为图节点** |
| `llm` | Singleton | 只提供 `ILlmPort` 实现；不暴露为图节点 |
| `logging` | Singleton | 只提供 `ISignalLogger` 实现 |
| `webui` | 独立进程 | 不作为模块出现在 host 的图里，只经 API |
| `contracts` | — | 纯类型，无实例 |

---

## 5. 一处修正：占用的本质是全局仲裁，不是频道属性

`link-design.md` 里我把占用放进了 `channel.*`，粒度也没说清。细想之后要分开：

| | `channel.state` | `channel.occupancy` |
|---|---|---|
| 语义 | 这个频道**是什么样**（亲和度、备注、配置） | 这个频道**现在被谁占着**（资源分配） |
| 天然粒度 | 每频道 | 跨频道仲裁 |
| 查询形状 | `get(channelKey)` | `query(channelKey)` / `list()` / `reap()` |
| 结论 | **PerChannel 实例** | **Singleton** |

理由：如果占用做成 PerChannel 实例，"谁占着所有频道"这个查询就得 fan-out 全部实例再聚合——而 fan-out 是只读聚合手段，用它来做**仲裁**（`acquire` 是写）是不允许的（§2 明确写了写不能 fan-out）。所以**占用必须是单例**。

这同时解决了之前发现的"三处状态分离"：

```
权威     channel.occupancy（单例，内存表 + version）
持久化   → IChannelStore（一个端口，两套后端：DB 表 或 json，由配置决定，不再并存）
观察     → occupancyTable 只读快照（webui / 诊断用）
```

`DB/Channel` 与 `ChannelState/*.json` 不再是两个真相，而是**同一个 store 端口的两个实现，二选一**。这直接回答了 `tree-cut.md` 的 Q5。

---

## 6. 代码缺口：要动的地方

| # | 缺口 | 现状 | 需要 |
|---|---|---|---|
| M1 | **多实例绑定** | `ModuleHost._implementations: Dictionary<string, object>`，`Bind(moduleId, impl)` 一模块一实例 | 改 `Dictionary<(string moduleId, string instanceKey), object>`；增 `BindFactory(moduleId, Func<InstanceContext, object>)` |
| M2 | **实例寻址** | 节点只有 `type`，没有 `instance` | 节点加 `instance` 字段 + 四种解析（§2）；`ModuleHost.Create` 按解析结果选实现 |
| M3 | **实例作用域** | `ModuleDescriptor` 没有 scope 概念 | 加 `[Module(Scope = ...)]`：Singleton / PerSession / PerChannel / PerAdapter / PerGraph |
| M4 | **生命周期事件集** | `[Lifecycle("mount"|"kill")]` 只有两个；`ILoopComponent` 另有一套 9 个钩子 | 统一成 §3.1 的一套；`[Lifecycle("shutdownRequested", Rejectable = true)]` |
| M5 | **生命周期事件分发** | 两套各自直接调方法 | 框架调用 + 发带外事件；只有 shutdown 收返回值 |
| M6 | **复合模块** | `ModuleAttribute` 只能标在 interface 上，无"内部是一张图"的表达 | 加 `Composite = true` + `GraphClassId`；对外接口 = 内部图的公开端口子集 |
| M7 | **fan-out 限制** | 无 | 加载期校验：`instance: "*"` 只能接只读口 |
| M8 | **`ILoopComponent` 与模块的关系** | 组件是 per-session 实例化的（`ComponentScope {Global, Loop}`），已是多实例——但不在图模块体系里 | 组件视为"PerSession 的模块"，`ComponentMeta` 并入 `ModuleDescriptor`；两条路合一 |

**M8 是最大的一块**：`ILoopComponent`（14 个成员）实际上已经是"per-session 多实例模块"了，只是用了另一套描述方式。合并而不是重写——组件的 `Meta` / `Tools` / 钩子分别对应模块的描述符 / 行为 / 生命周期事件。

---

## 7. 一个必须承认的张力

图的 `Evaluate` 是**无状态求解**（每次给输入、返输出），而模块是**有状态实例**。

这两者的桥已经存在：`ModuleHost` 把 `[Module]` 接口的方法变成节点，节点持 `implementation` 引用，状态在实现里。所以：

- **状态不在节点上，在实现实例上** → `GraphInstance.Variables` 只放"图自己的变量"（决策用），不放业务状态
- **节点是适配器，模块是实现** → 生命周期事件打在实现上，节点无生命周期
- 推论：**同一个模块实例可以被多张图引用**（因为它不属于任何一张图）。这正是"会话实例也是模块"的前提——外部图能引用 `session.actor#42`，是因为 `session.actor#42` 不归外部图所有。

这条不明确的话，M1/M2 做完就会出现"每个图实例各自 new 一个 session"的错误实现。

---

## 8. 未验证

| id | 问题 |
|---|---|
| Q12 | `[Module]` 现在只允许标在 interface 上（`AttributeTargets.Interface`）。复合模块要标在哪？interface 上（内部图作为实现细节）还是 class 上？ |
| Q13 | `ModuleDescriptorGenerator`（399 行）是否支持 `Scope` / `Instance` 的投影？未读全文。 |
| Q14 | `module.<id>.read.<prop>` 节点在有多个实例时，`ModulePropertyReadNode` 怎么知道读哪个实例？现有实现假定单实例。 |
| Q15 | `turn` 的 `Activated`/`Deactivated` 是唯一高频生命周期事件（每轮一次）。走带外事件会不会成为热点路径？可能需要按"每 N 拍"或"仅慢轮"采样。 |
| Q16 | 组件的 `OnBeforeInvokeAsync` / `OnAfterInvokeAsync` 与 `turn` 的拍边界是否重合？如果不重合，合并 M8 时会丢语义。 |

**未验证声明**：本文基于静态阅读，未 build / 未 run / 未 test。§4 的表格里，行为名与数据名是**按职责推断的**，不全是现有代码的真实签名——尤其是 `channel.*` 与 `mind`，现有实现里这些行为分散在 `ChannelContextService` / `ChannelAccessImpl` / `MindEngine` 中，没有以模块方法的形式存在。落地前必须逐条核对。
