# session 拆分细则

> 承接 `module-catalog.md`。针对 `AgentCoreProcessor/Engine/Session`（14 文件 4361 行）+ `Engine/Context`（624 行）的细致拆分。
> 未实现、未编译。本文是设计，不是已完成的改动。

---

## 0. 结论先说

1. **20 个部件**：12 个 PerSession（session 类型图内部）+ 8 个 host 侧 Singleton。
2. **`snapshot` 不是一个部件，是一份契约** —— 快照字段横跨 7 个部件，做成一个部件就是第二个上帝类（§5.2）。
3. **`ApplyToolEffectAsync` 是横切中介**，它揭出 5 条此前没记录的耦合（§5.1）。
4. **顺序：先对齐形状（ALC），再拆目录**（`migration-floating-context.md` §5 的理由在这里被实证了）。

---

## 1. 现状清单

| 文件 | 行数 | 主要职责 |
|---|---:|---|
| `RuntimeSession.cs` | 1591 | 会话运行时全部：装配、core 切换、组件、频道、启动、一拍主循环、持久化、生命周期 |
| `SessionEngine.cs` | 1360 | 子引擎：建会话（`CreateSession` 占 156 行）、池、claim、升级、子会话关系、调度任务、恢复 |
| `SessionPersistence.cs` | 441 | spec/state/history 三文件的读写 |
| `SessionTypeProfiles.cs` | 340 | 会话类型 profile |
| `SessionAgentHost.cs` | 115 | **上下文宿主**（名字误导，见 §5.4） |
| `Brain/LegacyRequestMapper.cs` | 95 | 到 SessionBrain 的请求映射 |
| `Brain/SessionBrainWiring.cs` | 85 | 接缝装配 |
| `SessionPool.cs` | 79 | 资源池（**全项目最干净的一段**） |
| `PortImpls.cs` | 64 | `IInputPort`/`IOutputPort` 实现 |
| `MailboxInputPort.cs` | 53 | 子会话邮箱端口 |
| `SessionResourceRegistry.cs` | 47 | 会话资源注册表 |
| `Brain/SessionBrainRuntime.cs` | 41 | static 宿主（R5 违反） |
| `ConnectedChannelState.cs` | 29 | 频道连接态（→ `channel`） |
| `SessionEngineSpawnCheck.cs` | 21 | — |
| **合计** | **4361** | |
| `Context/ChannelContextService.cs` | 521 | → `channel`（见 `module-catalog.md`） |
| `Context/SystemContextDataSource.cs` | 46 | `IChannelDataSource` 实现 |
| `Context/IChannelDataSource.cs` | 31 | 窄接缝（设计良好） |
| `Context/ChannelContextState.cs` | 26 | → `channel` |

`RuntimeSession.cs` 有 **66 个 private 字段 / 26 public + 37 private 方法**，无 `partial`。

---

## 2. PerSession 的 12 个部件（session 类型图内部）

| # | 部件 | 方法来源（行号） | 职责一句话 | 私有状态 |
|---|---|---|---|---|
| 1 | `session.actor` | setter ×7（66,70,116,158,280,281,282）+ `AddInputPort`(451) `AddOutputPort`(452) | **薄门面**：持状态与端口表，不干实事 | `status`、`sessionType`、`parentId`、`roundCount` |
| 2 | `step` | `RunAsync`(885–1305) **420 行** | 一拍主循环：突发计数 → 推理 → 工具 → 判定 | `burstRound`、`phase`、`_forceExitAttempts` |
| 3 | `activation` | `WaitForActivationAsync`(1402) `WaitForActivationCoreAsync`(1410) `ResetAllMpQueueCounts`(1474) | 等激活：权重累积、唤醒信号 | `weightSum`、`wakeSignaled` |
| 4 | `inbox` | `DrainInputPortsRaw`(1343) `AdvanceChannelCursor`(1330) `BuildPortSpecSnapshotForCompact`(414) + `PortImpls`(64) + `MailboxInputPort`(53) | 排空端口、频道游标 | `portTable`、`cursors` |
| 5 | `output` | `SendOutput`(1305) | 出向：产 intent，不直接发 | — |
| 6 | `core-switch` | `ConfigureCores`(165) `BuildSessionPrompt`(200) `AttachSessionPrompt`(216) `SwitchCore`(232) `SetActiveCore`(257) `GetOrCreateCore`(268) | 多 core 管理与切换 | `allowedCores`、`activeCore`、`coreInstances` |
| 7 | `components` | `InitComponents`(291) `RefreshToolList`(318) `Enable`/`Disable`(810,813) `ToggleComponentAsync`(817) `GetComponentIdsForCompact`(1590) | 组件宿主 | `componentIds`、`toolList` |
| 8 | `bootstrap` | `BootstrapAsync`(560) `DescribeInitResult`(594) `WriteJournalFromWaitAsync`(700) | 启动序列：跑 `InitActions` 并应用副作用 | `initActions` |
| 9 | `snapshot` | `BuildStateSnapshot`(1511) `RestoreFromSnapshot`(381) `PersistStateSync`(541) `PersistRoundAsync`(1495) `PersistStateAsync`(1556) `BuildChannelCursors`(1530) + `SessionPersistence`(441) | **聚合与版本迁移**（不拥有字段，见 §5.2） | `snapshotVersion` |
| 10 | `lifecycle` | `RequestStop`(826) `MarkCompletedForCleanup`(833) `SetWaitUntil`(835) `TouchActivity`(836) `WakeUp`(838) `InjectWaitTimeoutExpired`(846) `InjectExternal`(856) `ExtractFinalOutput`(872) | 停止/唤醒/超时/外部注入 | `waitUntil`、`lastActivity` |
| 11 | `children` | `AddChild`(1585) `RemoveChildFromState`(1579) | 子会话关系的**会话侧** | `childIds` |
| 12 | `resources` | `SessionResourceRegistry`(47) | 会话资源注册与通知 | `resourceTable` |

同时只有**一个**活跃 `step`。这是 `session.actor`（活很久）与 `step`（一拍一个）必须分开的直接理由。

---

## 3. host 侧 8 个 Singleton

| # | 部件 | 方法来源（行号） | 职责 |
|---|---|---|---|
| 13 | `session.pool` | `SessionPool`(79) + `FindPoolBySession`(336) `GetPoolsSnapshot`(541) `RemoveEmptyPool`(844) | 资源池树、池内活跃 session |
| 14 | `session.assembly` | `CreateSession`(87–243，**156 行**) `ComputeFinalComponents`(243) | 会话装配：按 profile 算组件集、建实例 |
| 15 | `session.type-profiles` | `SessionTypeProfiles`(340) | 类型 profile 解析与缓存 |
| 16 | `session.escalate` | `CreateExpressSessionForChannel`(443) `CreateFromEscalateAsync`(470) `ReclaimIdleExpressSessions`(859) | 升级路径 + 空闲回收 |
| 17 | `session.subagent` | `CreateSubPool`(258) `GetActiveChildren`(564) `ResolveActiveSession`(581) `ResolveEffectiveParent`(590) `OnChildCompleted`(597) | 子会话的**全局**关系 |
| 18 | `session.brain-bridge` | `Brain/`(221) + `SessionAgentHost`(115) | SessionBrain 接缝 + 上下文宿主 |
| 19 | `session.scheduler` | `AddScheduledTask`(881) `GetScheduledTasks`(886) `AddTodo`(893) `GetPendingTodos`(898) `CompleteTodo`(911) | → **并入 `recurring`** |
| 20 | `channel.claim` | `SubscribeChannel`(675) `ClaimChannel`(688) `TakeOverChannelPool`(712) `ReleaseChannel`(724) `GetFullClaimer`(735) `IsChannelClaimed`(742) | → **迁往 `channel`**（已定，见 C12） |

---

## 4. session 类型图（装配形状）

```
session.actor#<sessionId>            ← 门面，只暴露 route/prompt/inject/requestExit/snapshot
├── step#<sid>                       ← 一拍
│   ├── core-switch#<sid>
│   ├── components#<sid>
│   ├── bootstrap#<sid>
│   ├── children#<sid>
│   └── context#<sid>                ← 见 module-catalog（装配 / 合并 / 回收 / 压缩）
├── activation#<sid>
├── inbox#<sid>
├── output#<sid>
├── lifecycle#<sid>
├── resources#<sid>
├── snapshot#<sid>                   ← 聚合者，自身无状态字段
└── (context / mind / vision 由 step 引用，不归 step 拥有)

host 侧（Singleton，不在上面这棵树里）：
pool · assembly · type-profiles · escalate · subagent · brain-bridge
channel.claim → 已迁出到 channel
scheduler → 已并入 recurring
```

---

## 5. 四条关键发现（有实证）

### 5.1 `ApplyToolEffectAsync`（611–700，90 行）是横切中介，揭出 5 条新耦合

它是一张「工具结果 → 控制流」的映射表，7 个 case：

| 工具 | 它此刻调到谁 | 这是第几条跨模块边 |
|---|---|---|
| `end_session` | `_sessionEngine.GetActiveChildren` → `session.subagent` | ① |
| `end_session` | `_sessionEngine.AnyResourceBlocksExit` → `session.resources` | ② |
| `end_session` | `_componentHost.QueryCanExitAsync` → `components` | ③ |
| `end_session` | 私有 `_forceExitAttempts`（强制退出计数，两次触发） | 自身状态 |
| `compact` | 直接 `EndSession`（新 session 已接管频道） | — |
| `wait` | `SetWaitUntil` + `WriteJournalFromWaitAsync` → **`mind` 的 Writer** | ④ |
| `escalate` | `_sessionEngine.CreateFromEscalateAsync` → `session.escalate` | ⑤ |
| `connect/listen/disconnect_channel` | 本会话的频道连接 → `channel` | 已有 |

**结论**：这 90 行不是"启动的一部分"（我上一轮把它归进 `bootstrap` 是权宜）。它是 `step` 的**工具结果解释**子步骤，位置在"工具执行之后、判定之前"。而且它调的那 5 个目标**必须端口化**——否则 `step` 会直连 `session.escalate` / `resources` / `subagent` / `mind` / `components` 五个部件，等于把现在的耦合原样搬进新结构。

新增端口：`ISubagentPort`(查活跃子会话)、`IResourcePort`(在飞资源)、`IComponentGate`(能否退出)、`IJournalPort`(写经历)、`IEscalatePort`(升级)。

> **⚠️ 已被 §7 推翻，不要照这里实施。** 读完 ALC 的 `ApplyPlanCalls` / `ApplyModeCalls` / `EndedGoal`（三个 **static 纯函数**）之后，更好的做法是：把这 90 行改成**纯函数解释器** `Interpret(batch, snapshot) → IReadOnlyList<StateMutation>`，由 `session.actor.apply(mutations)` 统一应用。
> 好处：5 条耦合直接消失（`step` 只产 mutation，不调任何部件）、可单测、与"图只产意图"同构。见 `module-io-spec.md` §7。

### 5.2 `snapshot` 不是部件，是一份契约

`SessionStateSnapshot` 的 14 个字段分别属于不同部件：

| 字段 | 所属部件 |
|---|---|
| `SessionId` / `Status` / `CreatedAt` | `session.actor` |
| `RoundCount` | `step` |
| `WaitUntil` / `LastActivity` | `lifecycle` |
| `CompressionTier` | `context` |
| `ActiveCoreConfig` | `core-switch` |
| `ConnectedChannelId` / `ConnectedMode` | `channel` |
| `ChannelCursors` | `inbox` |
| `ChildSessionIds` / `EffectiveParentId` / `ForwardedTo` | `children` / `subagent` |

**所以 `snapshot` 只能是聚合者**：每个部件各自提供 `snapshot()` / `restore(blob)`，`session.snapshot` 负责拼装、版本迁移（`StateFormatVersion = 2`）、失败回滚。若让它拥有这些字段，它立刻变成第二个上帝类。

### 5.3 `SessionPersistence` 里两个真实缺口

| 项 | 现状 | 问题 |
|---|---|---|
| `ArchiveAsync`(378) | `=> Task.CompletedTask` —— **空实现** | 归档功能名义存在、实际不做 |
| `LoadHistory(maxLines = 1000)`(332) | 只读前 1000 行 | 长会话恢复会丢历史（是否自觉截断未知，见 Q26） |

另：`TrimForPersistence`(153) 在落盘前裁剪消息——这与"回收/压缩"是**第三套**机制（前两套是 `Reclaim` 与层压缩）。别把它们混成一个。

### 5.4 `SessionAgentHost` 是上下文宿主，不是会话宿主

它做的三件事全是上下文：`BuildStartAsync`（起始）、`BuildRoundAsync`（每轮）、`BuildChannelContextAsync`（连接频道时）。名字里的 "Host" 让人以为它是会话的生命周期宿主。

**对齐 ALC 后它应整体并入 `context`**，`RuntimeSession.SetAgentHost`(70) 这条注入边消失。这也是为什么 §2 的 `step` 不拥有 `context` —— 上下文装配不归推进器。

---

## 6. 新增/变更的端口（全部要在 `contracts` 里）

| 端口 | 提供方 | 消费方 | 用途 |
|---|---|---|---|
| `IChannelPort` | `channel` | `session` / `step` / `adapters` | 占用申请/释放、频道状态（已定） |
| `ISubagentPort` | `session.subagent` | `step` | `getActiveChildren(sid)` |
| `IResourcePort` | `session.resources` | `step` | `anyInFlight(sid)` |
| `IComponentGate` | `components` | `step` | `canExit()` |
| `IJournalPort` | `mind` | `step`（`wait` 工具） | `write(raw)` |
| `IEscalatePort` | `session.escalate` | `step`（`escalate` 工具） | `escalate(session, reason)` |
| `IAxisPort` | `axes` | `context` / `step` / `webui` | 轴值、冻结快照、等级解释 |
| `ISnapshotPort` | 每个有状态部件 | `session.snapshot` | `snapshot()` / `restore(blob)` |

**`ISnapshotPort` 是关键**：它让 §5.2 的"聚合者"成立——`snapshot` 不认识任何具体部件，只遍历注册表。

---

## 7. 拆分顺序（对齐在前）

前置判断见 `migration-floating-context.md` §5：**先对齐形状，再拆目录**。这里的实证是 §5.4（上下文宿主该并入 context）与 §5.1（工具结果解释该归 step）——**若先拆，这两处都会按旧形状切错**。

| 阶段 | 做什么 | 产出 | 风险 |
|---|---|---|---|
| **A. 对齐形状**（仍在 1 个 csproj 内） | | | |
| A1 | `axes` 模块 + 轴表迁入 | 442 行蓝本 → 本项目 | 低 |
| A2 | `PromptAssembly` / `PromptSlot` / `PromptContribution` 进 contracts | 顺序契约落地 | 低 |
| A3 | `context` 四件事分开；`SessionAgentHost` 并入 | 上下文装配独立 | 中 |
| A4 | `step` 边界确定（含 `ApplyToolEffectAsync` 归位） | 一拍 = 420 行主循环 + 工具结果解释 | 中 |
| **B. 拆 session**（切文件，不切 csproj） | | | |
| B1 | 12 个 PerSession 部件切文件 | 1591 → 12 个文件 | 中 |
| B2 | 8 个 Singleton 部件切文件 | 1360 → 8 个文件 | 中 |
| B3 | 5 条新端口落地（§5.1） | `step` 不再直连 5 个部件 | **高** |
| B4 | `ISnapshotPort` 注册表 | `snapshot` 退化为聚合 | 中 |
| **C. 拆项目** | | | |
| C1 | `session` 复合模块切 csproj；内部部件先留在同一程序集 | 模块边界可见 | 中 |
| C2 | 其余模块按 `tree-cut.md` 的 C1 拆 16 个 csproj | 装配树成立 | 高 |
| **D. 连网** | | | |
| D1 | `link-design.md` 的 L1 影子图对拍 | 验证"拍内一律 Inline" | **高**（可能证伪） |
| D2 | 内置图 JSON 化 | 内置 = 自定义同一方案 | 中 |

**A 阶段不动任何文件结构**，只移动代码归属与新增契约——这是"先对齐"的全部含义。

---

## 7b. 剩余切片的锚点（`RuntimeSession.cs` 已缩到 1212 行，此处是一次性定位）

| 块 | 行号（定位时刻） | 内容 | 目标文件 |
|---|---|---|---|
| bootstrap | 415–666 | `BootstrapAsync` / `DescribeInitResult` / `ApplyToolEffectAsync` / `BuildToolEffectContextAsync` / `WriteJournalFromWaitAsync` / `AppendGlobalPromptSections` / `AppendStartReferenceSections` / `ApplyContextMutation` | `RuntimeSession.Bootstrap.cs` |
| 组件启停 | 668–686 | `EnableComponent` / `DisableComponent` / `ToggleComponentAsync` | 并入 `RuntimeSession.Components.cs` 尾部 |
| 频道连接 | 318–404 | `ConnectChannelAsync` / `DisconnectChannel` / `PersistStateSync` | `RuntimeSession.ChannelConnect.cs` |
| 持久化 | 1122–1211 | `PersistRoundAsync` / `BuildStateSnapshot` / `BuildChannelCursors` / `EstimateChars` / `PersistStateAsync` / `GetComponentIdsForCompact` | `RuntimeSession.Persistence.cs`（= B-2，**最后做**） |
| step 主循环 | 689–1115 | `RunAsync`（约 426 行） | B-4，不只是搬文件，要拆方法 |

⚠️ **行号是"定位时刻"的快照，每次切割后都会变**。下一刀之前必须重新 `grep -n` 取当前值——
这一点是 B-3b 第一次失败的直接原因（复用了旧偏移量，切进了 `ToggleComponentAsync` 中间）。

### 已落地的部件（切文件完成）

| 文件 | 行数 | 部件 |
|---|---:|---|
| `RuntimeSession.Inbox.cs` | 111 | 端口排空 + 频道游标 |
| `RuntimeSession.Output.cs` | 53 | 出向 |
| `RuntimeSession.Activation.cs` | 114 | 等激活 |
| `RuntimeSession.Lifecycle.cs` | 93 | 停止/唤醒/超时/外部注入 |
| `RuntimeSession.Children.cs` | 48 | 会话侧父子链 |
| `RuntimeSession.CoreSwitch.cs` | 157 | 多核白名单与切换 |
| `RuntimeSession.Components.cs` | 98 | 组件加载与工具表刷新 |

## 8. 未验证
| id | 问题 |
|---|---|
| Q26 | `LoadHistory(maxLines = 1000)` 是有意截断还是缺陷？未读实现（`SessionPersistence.cs:332`） |
| Q27 | `ArchiveAsync` 空实现（`:378`）—— 归档是否已由别处承担（`backup-*.tar.gz` 那套脚本？） |
| Q28 | `session.assembly.CreateSession` 156 行里是否还藏着别的职责（我只看了方法名与行数，未读正文） |
| Q29 | `_forceExitAttempts` 的两次触发语义（第二次强行退出）是否需要保留成策略，还是属于"审批逃生口" |
| Q30 | `SessionEngine.RunAsync`(922–1035，113 行) 的调度循环与 `recurring` 的边界：是搬过去还是留个 tick 适配器 |

**未验证声明**：本文基于静态阅读（方法签名 + 行号 + `ApplyToolEffectAsync`/`SessionPersistence`/`SessionAgentHost` 正文），未 build / 未 run / 未 test。§2 与 §3 的"职责一句话"有推断成分；`RunAsync` 420 行与 `CreateSession` 156 行我只读了分段注释，未通读。
