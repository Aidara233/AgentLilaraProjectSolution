# 模块 I/O 规约：生命周期 / 数据源 / 调用接口 / 图节点

> 承接 `module-catalog.md`、`session-decomposition.md`、`link-design.md`。
> 用途：**直接给"做图"用** —— 每个模块的节点名、入端口、出端口、生命周期事件都在这里。
> 未实现、未编译。

---

## 0. 三个先说清的核实结论

### 0.1 重新定案：`step` = 突发（N 次模型调用），`turn` = 一次模型调用

这一条我改过两次，第三次是通读 `RunAsync` 986–1310 **全文**之后定的，以这一版为准：

**证据（`RuntimeSession.cs`）**

| 位置 | 事实 |
|---|---|
| `:986` | `while (IsAlive && !ct.IsCancellationRequested)` 是**突发循环**，不是单次调用 |
| `:1003` | `agentRound = 0;` —— 突发开始时重置"突发内轮次" |
| `:1006` | `_roundCount++;` —— **每次突发 +1**，所以 `_roundCount` 是**突发数**，不是"模型调用数" |
| `:1144` | `agentRound++;` —— 每次 while 迭代 +1，即**突发内第几次模型调用** |
| `:1279-1288` | **模型无工具调用时不 break**，注入「你没有调用任何工具」提醒后**继续下一迭代** |
| `:1276` | 只有 `endBurst` 才 `awaitingInput = true; continue;`（回等待、保活握 claim） |
| `:1268` | 只有 `endSession` 才 `break`（真正终结） |

**所以三者的界线是**：

```
step（突发）= 从 WaitForActivation 返回 → 直到 endBurst 或 endSession
  ├── turn#1（一次模型调用 + 其工具批）
  ├── turn#2 …            ← 无工具调用也会继续，直到模型给出 end_session / wait
  └── 判定 → 回等待 或 终结
```

**为什么 ALC 的 `StepAsync` 只有一个 `foreach`（:443）却也能是"一步"**：因为 ALC 把循环放在**调用方**——`CommandDispatcher` 反复调 `StepAsync`，每次一个 `StepOutcome`；而本项目的循环在 `RunAsync` 内部。**两者概念等价，只是循环位置不同**。

→ 结论：`step`（突发）与 `turn`（单次调用）**是两层，都要保留**；`turn` 不作为对外模块，是 `step` 的内部结构，但它的边界必须在 `step` 里显式存在（否则"哪一次调用出的错"无法归因）。
→ 我上一版写的「`turn` 和 `step` 是同一个东西」**作废**。

### 0.2 计划树推进 / 模式切换 / goal 终止判定都在 `step` 内部，且是**纯函数**

| ALC 里的方法 | 行号 | 形态 |
|---|---|---|
| `ApplyPlanCalls` | 1323 | `static (SessionPlan? Plan, bool Changed) ApplyPlanCalls(...)` —— 输入调用列表 + 当前计划，输出新计划 |
| `ApplyModeCalls` | 1367 | `static (string? Mode, bool Changed)` |
| `EndedGoal` | 1406 | `static bool EndedGoal(batch, calls)` —— 只看有没有执行 `finish_goal` |
| `WithStandardInitiative` | 1416 | `static IReadOnlyDictionary<string,string>?` —— **复制新字典，不原地改** |

→ 这给出一个比"端口化 5 个部件"更好的方案（见 §5.4）。

### 0.3 `StepOutcome` 证明"只交结果、不做副作用"

`StepOutcome`（:1481）带出来的东西全部是**交回去让调用方做**，`Stepper` 自己不碰：

| 字段 | 交给谁做 |
|---|---|
| `GoalEnded` | 调用方发 `axis.changed`（撤掉目标标记）。注释明确：「不要据此掐掉下一次推进——用户要的是再多跑一步让模型收尾」 |
| `Compacted` | 调用方发 `session.compaction.completed` |
| `Spawns` | 调用方真正建会话（它才有 `PluginHost`/事件总线/`Start`） |
| `ReleasedClaims` / `ReleasedLines` | 调用方发事件 |
| `ToolResults` | **记录那一份**（未回收、未截断、未加框架），回收是**渲染期**的事 |

这与 `link-design.md` 的「图不执行动作，只产出意图」是同一思想，而且**已经落地过一遍**——可以直接照抄。

---

## 1. 命名规则（做图必须遵守）

| 项 | 规则 | 例 |
|---|---|---|
| 模块类型 | `[Module("<domain>.<name>")]` | `session.step`、`channel.occupancy` |
| 方法节点 | `module.<moduleId>.<camelMethod>` | `module.session.step.run` |
| 读属性节点 | `module.<moduleId>.read.<camelProp>` | `module.session.actor.read.roundCount` |
| 写属性节点 | `module.<moduleId>.write.<camelProp>` | `module.channel.state.write.affinity` |
| 实例引用 | `literal` / `$beat.<scope>` / `singleton` / `*` | `$beat.session`、`singleton` |
| 内建节点 | `source.*` / `logic.*` / `sink.*` | `sink.send` |
| 内置连接 | `LinkSpec.Builtin = true` | UI 只读 |

---

## 2. 根与基础设施

| 模块 | 实例 | 生命周期 | 数据源（入） | 调用接口（出） | 图节点 |
|---|---|---|---|---|---|
| `contracts` | none | — | — | — | 无（纯类型） |
| `host` | singleton | `Created→Mounted→Ready`；`ShutdownRequested`（**可拒**：有活跃会话）`→Killing→Killed` | `file:config/*.json`、`env` | `boot` / `shutdown` / `rebind`；`moduleInventory`(读)、`uptime`(读) | `module.host.read.*` |
| `data` | singleton | `Mounted`（建连接、跑迁移） | `→DB:sqlite`、`→file:Storage/` | 只提供 `I*Repository` 实现，**不作为图节点** | 无 |
| `llm` | singleton | `Mounted`（加载 provider 配置） | `←ILlmPort 请求`、`→网络:provider` | `invoke(messages, params)` / `stream(...)` | 无（经端口） |
| `logging` | singleton | `Mounted`；`Killed`（flush） | `←ISignalLogger` | 无 | 无 |
| **`axes`** | **singleton** | `Mounted`（核心表 + 插件轴）`→Sealed`（注册封闭）`→Reloaded`（可选） | `←plugin-host.registerAxis`、`→file:state.json`(轴值) | `valuesOf(axis)` / `labelOf` / `tryParse` / `descriptionOf` / `numberOrNull` / `defaults()` / `freeze(values)` / `renderOrder`(读) / `names`(读) / `numericAxes`(读) | `module.axes.*` |

**`axes.freeze(values)` 是每拍一次的关键调用** —— 它产出冻结快照，`context` 与 `step` 共用同一个快照（否则同一步内两次渲染会不一致）。

---

## 3. 连接与工具

| 模块 | 实例 | 生命周期 | 数据源（入） | 调用接口（出） | 图节点 |
|---|---|---|---|---|---|
| `plugin-host` | singleton | `Mounted`（扫目录）`→Reloaded`（热更）`→Killed`（全卸 + 注册表反注册） | `→file:Plugins/`、`→ALC` | `load` / `unload` / `reload` / `invoke(tool,args)` / `list()`；`loadedPlugins`(读) `toolRegistry`(读) | `module.pluginHost.*` |
| **`builtin-tools`** | **singleton** | `Mounted`（建表） | `←contracts` 实现；`→host` 注入的服务 | `invoke(name,args)` / `list()`；`toolNames`(读) | `module.builtinTools.*` |
| `adapters.<id>` | **per-adapter** | `Created→Mounted`；`Disabled`/`Enabled`（掉线不注销）；`Crashed(reason)`（可重连）；`Killed` | `→网络:onebot/http` | **只出** `event: InboundEvent`；`send(channelKey, content)` / `probe()`；`connected`(读) `platform`(读) `owns`(读写) | `module.adapters.send` 等，实例 `literal:<id>` |
| `commands` | singleton | `Mounted` | `←text`（来自 adapters / webui） | `handle(text)` / `list()`；`registry`(读) | `module.commands.*` |
| `webui` | **独立进程** | 随进程 | `←HTTP/WS`（DTO） | `→host` API | 无（api 边） |
| `graph` | per-graph-instance | 类型侧 `Loaded→Validated→Sealed`；实例侧 `Created→Mounted→Active/Deactivated→Snapshot→Killed`；`Crashed(reason)` | `←InboundEvent` | `dispatch` / `validate` / `reset` / `snapshot` / `restore` / `swapClass` / `inspect`；`variables`(读) `lastIntents`(读) `lastResult`(读) `nodeCount`(读) `linkCount`(读) `classId`(读) `instanceKey`(读) | `module.graph.*` |

**`outbound.router`** 是连接模块里的一个内建节点（不在上表，属于 `graph` 的内建节点集）：入 `intent: ActionIntent`，按 `AdapterId` 前缀或订阅记录选适配器。

---

## 4. 频道域

| 模块 | 实例 | 生命周期 | 数据源（入） | 调用接口（出） | 图节点 |
|---|---|---|---|---|---|
| `channel.occupancy` | **singleton** | `Mounted`（从 `IChannelStore` 重建）；`Slept`（暂停 reaper）；`ShutdownRequested`（**可拒**：仍有活跃占用） | `←IChannelStore`、`←acquire/release` | `acquire(channelKey,sessionId,level)→Grant/Deny` / `release` / `query(channelKey)` / `reap(olderThan)`；`table`(读) `version`(读) | `module.channel.occupancy.*` |
| `channel.state` | **per-channel** | `Created`（频道首现）`→Mounted`（加载）`→Snapshot/Restore→Killed`（归档） | `←IChannelStore`、`←patch` | `patch(StatePatch)` / `reset()`；`affinity`(读写) `memo`(读写) `config`(读写) `lastActiveAt`(读) | 实例 `$beat.channel` |
| `channel.context` | **singleton（无状态）** | 无（纯查询） | `←IChannelDataSource`（DTO，不含 DB 实体） | `build(channelKey, limit)→ChannelContextFragment` | `module.channel.context.build` |
| `channel.route` | **singleton** | `Mounted`（加载规则）；`Reloaded`（规则热更） | `←InboundEvent`、`←occupancy.snapshot` | `decide(event, snapshot)→RouteDecision`；`rules`(读写) | `module.channel.route.*` |

---

## 5. 会话域

### 5.1 PerSession 12 个

| 模块 | 生命周期 | 数据源（入） | 调用接口（出） | 图节点 |
|---|---|---|---|---|
| `session.actor` | 全套；`ShutdownRequested`**可拒**（长任务）；`Restored` 跳过起始 prompt；`Crashed` 留现场 | `←route/grant/input/mailbox` | `route` / `prompt` / `inject` / `requestExit` / `snapshot` / `restore`；`status`/`sessionType`/`parentId`/`roundCount`/`contextUsage`（读） | `module.session.actor.*`，实例 `$beat.session` |
| `step` | `Activated`（**突发开始**）→ `Deactivated`（`endBurst` 或 `endSession`）；`Snapshot`（仅突发边界） | `←activation.wait` 返回；`←axes.freeze`；`←context.*`；`←builtin-tools/plugin-host`（工具表） | 驱动 `turn` 循环；`phase`(读：等待/排空/推理/工具/判定)、`burstRound`(读)、`lastOutcome`(读) | `module.session.step.run`，实例 `$beat.session` |
| `turn`（**step 内部，不对外**） | 无独立生命周期 | `←inbox`（中途排空）、`←context`（压缩提示） | `llm.invoke` / `tools.invoke` / `interpret(...) → StateMutation` | 无独立节点；图上表现为 `module.session.step.run` 的一次求值，但 `phase` 必须能区分它 |
| `activation` | `Mounted`；`Paused/Resumed` | `←inbox.tryPeek`、`←WakeUp` | `wait()` / `wake()`；`weightSum`(读) `wakeSignaled`(读) | `module.session.activation.wait` |
| `inbox` | `Mounted`；`Killed`（Complete 写入端） | `←adapters`、`←mailbox`、`←SessionEngine.SubscribeChannel` | `addPort` / `drain()`→`InputBatch`；`portTable`(读) `cursors`(读) | `module.session.inbox.drain` |
| `output` | 无 | `←step` | `send(text)` → `ActionIntent.SendMessage` | `sink.send`（内建汇） |
| `core-switch` | `Mounted`；`Switched`（每次切换） | `←cores.json` 配置 | `switch(name)` / `list()`；`allowedCores`(读) `activeCore`(读) | `module.session.coreSwitch.*` |
| `components` | `Mounted`；`Enabled/Disabled`（每个组件） | `←plugin-host`（组件清单） | `init` / `toggle(name,on)` / `toolList()` / `canExit()`；`componentIds`(读) `toolList`(读) | `module.session.components.*` |
| `bootstrap` | `Mounted`（跑 `InitActions`） | `←CreateSessionSpec.InitActions` | `bootstrap()`；`initActions`(读) | `module.session.bootstrap.run` |
| `snapshot` | `Mounted`（注册各部件）；`Snapshot`（每轮）；`Restore`（启动） | `←ISnapshotPort` 注册表（**遍历，不认识具体部件**） | `snapshot()` / `restore(blob)` / `migrate(version)`；`stateFormatVersion`(读) | `module.session.snapshot.*` |
| `lifecycle` | 事件源本身 | `←RequestStop/WakeUp/timer` | `requestStop(reason)` / `wake()` / `setWaitUntil` / `injectExternal(text)` / `extractFinalOutput()`；`waitUntil`(读写) `lastActivity`(读) | `module.session.lifecycle.*` |
| `children` | `Mounted` | `←session.subagent`（全局关系） | `addChild(id)` / `removeChild(id)`；`childIds`(读) | `module.session.children.*` |
| `resources` | `Mounted`；`NotifiedCompacting`/`NotifiedFreed` | `←ISessionResource` 注册 | `register` / `unregister` / `notifyCompacting` / `notifyFreed` / `anyInFlight(sid)` | `module.session.resources.*` |

### 5.2 host 侧 Singleton 8 个

| 模块 | 生命周期 | 数据源（入） | 调用接口（出） |
|---|---|---|---|
| `session.pool` | `Mounted`；`PoolCreated`/`PoolRemoved` | `←host` | `findPool(sessionId)` / `pools()` / `removeEmpty()` |
| `session.assembly` | `Mounted` | `←CreateSessionSpec`、`←type-profiles`、`←axes.defaults` | `create(spec)→sessionId`（内部 156 行：算组件集、建实例） |
| `session.type-profiles` | `Mounted`；`Reloaded` | `→file:Storage/SessionTypes/*.json` | `get(type)→profile` |
| `session.escalate` | `Mounted` | `←step.escalate` 意图 | `escalate(session,reason)` / `reclaimIdle()` |
| `session.subagent` | `Mounted` | `←spawn` / `←children` | `createSubPool` / `activeChildren` / `resolveActive` / `resolveParent` / `onChildCompleted` |
| `session.brain-bridge` | `Mounted` | `←SessionBrain`（`Lilara.Core`） | `snapshot(request)` / 影子对拍 |
| `session.scheduler` | → **并入 `recurring`** | | |
| `channel.claim` | → **迁出到 `channel`**（C12） | | |

---

### 5.3 `step` 的 12 个阶段（逐行校准 `RunAsync` 986–1305）

这一节替代前面那个粗糙的 `phase`（组上下文/调模型/执行工具/落库）。**每个阶段都标了归属模块**——这就是做图时的边。

| # | 阶段 | 行号 | 归属模块 | 备注 |
|---:|---|---|---|---|
| 1 | 等待激活：`WaitForActivationAsync` → `TouchActivity` → `SetWaitUntil(null)` → `agentRound=0` → `_roundCount++` → `ApplyRoundPricing` | 989–1006 | `activation` + `step` | `_roundCount` 是**突发数**，`agentRound` 是**突发内调用数** |
| 2 | 突发入口组件通知：`OnBeforeInvokeAsync` → `BuildPromptSections` → 注入 `[组件通知]` → 持久化 | 1007–1025 | `components` + `context` + `snapshot` | 有通知时 `hasComponentNotification=true`，避免阶段 4 重复做 |
| 3 | 收集输入：`DrainInputPortsRaw` → `_host.BuildRoundAsync` → `AdvanceChannelCursor` → `PersistRoundAsync`；失败回退裸内容 | 1027–1078 | `inbox` + `context` + `snapshot` | **第一次排空** |
| 4 | 推理前准备：组件通知（若未做）/ `_pendingInjections` 注入 / **压缩提示 L1·L2 注入** / **L3 锁定工具表** / 设 `EngineType`+`AdditionalTools` / 锁 `roundCore` | 1080–1145 | `context` + `core-switch` + `step` | L3 锁定会**清空 `_tools` 只留 `compact`**，见 §5.4 |
| 5 | 中途注入：`DrainInputPortsRaw(CancellationToken.None)`（非阻塞）→ `BuildRoundAsync` → 游标 → 持久化 | 1147–1181 | `inbox` + `snapshot` | **第二次排空**，同一迭代内 |
| 6 | 模型调用：`Signal.Open("Session 轮次 #N")` span → `roundCore.InvokeAsync(_history, EngineMode.Working)` → `SessionBrainRuntime.Observe`（影子） | 1183–1202 | `step`(turn) + `brain-bridge` | 失败 → 写 `[错误]` 消息 → `continue`（不停会话） |
| 7 | assistant 消息入历史 | 1204 | `step` | 稍后可能被阶段 8 重写为 native `tool_use` |
| 8 | 工具执行：`ToolExecutor.ExecuteAsync` → `FormatAssistant`（携带 `tool_use_id`）→ `FormatToolResults` → `PersistRoundAsync` | 1206–1247 | `builtin-tools` / `plugin-host` + `step` + `snapshot` | resolver 三级：`_tools` → `_componentHost.TryGetTool` → `ToolRegistry.Get` |
| 9 | **工具结果解释**：逐个 `ApplyToolEffectAsync(call, result)` → 汇总 `endBurst` / `endSession` | 1249–1261 | **`interpret`（纯函数）** | 见 §7 的改写方案 |
| 10 | 内联 wait：`results.Any(r => r.RequestWait)` → `endBurst = true` | 1263–1264 | `interpret` | `speak` 含 `[wait]` 占位符时触发 |
| 11 | 判定：`endSession` → `break`（终结）；`endBurst` → `_forceExitAttempts=0`、`awaitingInput=true`、`continue`（回等待保活）；**无工具调用 → 注入提醒后继续下一迭代** | 1266–1290 | `step` | 这是 `step` 与 `turn` 分层的证据 |
| 12 | 收尾（`finally`）：置 `Completed`、关 `lifeCtx`、`OnCompleted?.Invoke(this)` → `SessionEngine` 释放 session-scope 图实例 | 1292–1305 | `lifecycle` + `graph` | 唯一的"会话终结 → 释放图实例"出口 |

### 5.4 通读之后新发现的三处隐藏耦合

| # | 耦合 | 位置 | 为什么是问题 |
|---|---|---|---|
| H1 | `context` → **工具表** | 1113–1125：L3 锁定时 `_tools.Clear()`，只留 `_compactTool`，并直接改 `_agentCore.AdditionalTools` | 上下文层**直接改写**了工具层的数据结构。拆模块后这条边必须变成显式的 mutation（`SetToolTable(only: compact)`），否则 `context` 会依赖 `builtin-tools` 的内部集合 |
| H2 | `step` → **`_agentCore` 字段直写** | 1139–1140、1122：每迭代写 `EngineType`、`AdditionalTools` | 推进器在改另一个对象的字段，而不是传参。应变成 `llm.invoke(request)` 的入参 |
| H3 | **两次排空**同一迭代 | 1027（入口）+ 1147（中途） | 图里 `inbox` 有**两个不同时序的**调用点。若只用一条边，会丢掉"中途注入"这个语义——需要两条边或一个 `phase` 约束 |



| 模块 | 行数（现状） | 组成 | 生命周期 | 数据源 | 调用接口 |
|---|---:|---|---|---|---|
| `mind.emotion` | 562 | `EmotionService`(192) + `EvidenceEngine`(188) + `EvidenceMath`(54) + `ConflictService`(128) | `Mounted`；`Snapshot`/`Restore`；`Slept`（衰减暂停） | `→DB:EmotionSnapshots`、`←observe(event)` | `observe` / `getSnapshot` / `resolve(conflict)` |
| `mind.memory` | 564 | `RetrieveService`(351) + `Resurrector`(118) + `EventSink`(95) | `Mounted`；`Snapshot`/`Restore` | `→DB:Claims/Journals/Fragments`、`→embedding` | `recall(query)→RetrieveResult` / `resurrect` |
| `mind.review` | 351 | `ReviewStage`(245) + `ReviewQueueService`(106) | `Mounted`；`StageCommitted` | `→DB:ReviewQueue` | `enqueue` / `preview` / `commit(item)` |
| `mind.write` | 237 | `Writer`(237) | `Mounted` | `←writeBatch/writeClaim/writeJournal` | `write(batch)` → claims/journals |
| `mind.extract` | 457 | `ExpressExtractor`(385) + `ExtractService`(72) + `IExtractionLlm` | `Mounted` | `←对话`、`←llm` | `extract(messages)→facts` |
| `mind` (壳) | 169 | `MindEngine`(132) + `SpawnCheck`(37) | `Mounted`/`Killed` | — | 编排上面 5 个；建议**并入 host**（它只是 `ISubEngine` 适配器） |
| `vision` | 797 | 4 文件 | `Mounted`；`Paused/Resumed`；`ShutdownRequested`（队列非空可拒） | `←ImageRef`、`→provider` | `analyze` / `ocr`；`phaseConfig`(读写) `queueDepth`(读) |
| `recurring` | 1798+ | `Worker`(1169)+`Source`(281)+`Messaging`(206)+`Timer`(142)+`scheduler` | `Mounted`（从库恢复）；`Slept`（全挂起，**不可拒**）；`Killed`（落库） | `→DB:ScheduledTasks/Todo`、`←tick` | `schedule` / `cancel` / `tick` / `addTodo` / `completeTodo`；`pendingCount`(读) `nextFireAt`(读) |

---

## 7. 修正：`ApplyToolEffectAsync` 应改成**纯函数**，而不是端口化 5 个部件

`session-decomposition.md` §5.1 里我说要为 `end_session`/`wait`/`escalate` 那 90 行引入 5 个端口。读完 ALC 的 `ApplyPlanCalls`/`EndedGoal` 后，这是个更差的做法——**ALC 用的是纯函数**：

```csharp
// 输入：这一批执行过的调用 + 当前会话快照
// 输出：状态变更意图（不执行）
static IReadOnlyList<StateMutation> Interpret(
    IReadOnlyList<(ToolCall Call, ToolResult Result)> batch,
    SessionPublicState snapshot);

public abstract record StateMutation;
public sealed record EndSession(bool Force) : StateMutation;
public sealed record EndBurst : StateMutation;
public sealed record SetWait(TimeSpan Duration) : StateMutation;
public sealed record Journal(string Raw) : StateMutation;
public sealed record Escalate(string Reason) : StateMutation;
public sealed record ConnectChannel(int ChannelId, ConnectionMode Mode) : StateMutation;
```

三个好处：

1. **5 条耦合直接消失**：`step` 不再调 `session.escalate` / `resources` / `subagent` / `mind` / `components`，只产 `StateMutation`。
2. **可单测**：纯函数，《输入 → 输出》一张表。
3. **与"图不执行动作、只产意图"同构**：`StateMutation` 和 `ActionIntent` 是一对——一个改会话内部状态，一个出到外部。

`_forceExitAttempts`（强制退出计数）也从 `step` 的字段变成**输入快照的一部分**（`snapshot.forceExitAttempts`），解释器据此返回 `EndSession(Force: true)`。

→ **B3 从「5 条端口」改为「1 个纯函数解释器 + 1 个 apply 出口」**，改动量更小、耦合更少。

---

## 8. 做图的下一步

有了 §2–§6，内置图可以直接写：

```jsonc
// graphs/builtin/ingress.json（节选，与用户图同格式）
{
  "classId": "builtin.ingress",
  "kind": "Static",
  "nodes": [
    { "id": "in",     "type": "module.channel.inbox.drain",      "instance": "$beat.channel" },
    { "id": "freeze", "type": "module.axes.freeze",              "instance": "singleton" },
    { "id": "ctx",    "type": "module.context.assemble",         "instance": "$beat.session" },
    { "id": "step",   "type": "module.session.step.run",         "instance": "$beat.session" },
    { "id": "interp", "type": "module.session.step.interpret",   "instance": "$beat.session" },
    { "id": "apply",  "type": "module.session.actor.apply",      "instance": "$beat.session" },
    { "id": "out",    "type": "sink.send" }
  ],
  "links": [
    { "from": "in",     "fromPort": "batch",    "to": "step",   "toPort": "input",    "builtin": true },
    { "from": "freeze", "fromPort": "snapshot", "to": "ctx",    "toPort": "axes",     "builtin": true },
    { "from": "ctx",    "fromPort": "assembly", "to": "step",   "toPort": "context",  "builtin": true },
    { "from": "step",   "fromPort": "outcome",  "to": "interp", "toPort": "outcome",  "builtin": true },
    { "from": "interp", "fromPort": "mutation", "to": "apply",  "toPort": "mutation", "builtin": true },
    { "from": "step",   "fromPort": "intent",   "to": "out",    "toPort": "intent",   "builtin": true }
  ]
}
```

---

## 9. 未验证

| id | 问题 |
|---|---|
| Q26 | `LoadHistory(maxLines=1000)` 是否有意截断 |
| Q27 | `ArchiveAsync` 空实现的归档是否由别处承担 |
| Q28 | `CreateSession` 156 行正文未读 |
| Q31 | `RunAsync` 986–1300 我只读了循环骨架与分段注释，**未通读 420 行**；`step` 的"阶段"划分（§5.1 `phase` 字段）需按实际代码校准 |
| Q32 | `mind` 的 6 个模块是否真的可独立——`EmotionService` 与 `ConflictService`、`RetrieveService` 与 `Resurrector` 的相互调用未追 |
| Q33 | `recurring` 并入 `session.scheduler` 后，`SessionEngine.RunAsync`(922–1035, 113 行) 的调度循环是搬过去还是留 tick 适配器 |

**未验证声明**：本文方法名与属性名是**按职责推断的规约**，不全是现有代码的真实签名。落地前每个模块都要对着源码核一遍签名。
