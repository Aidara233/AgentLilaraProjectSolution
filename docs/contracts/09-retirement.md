# 淘汰清单（09）

> 依据：`00-decisions.md`（分层与解耦、存储、路由、信源、图、授权、WebUI、插件）与 `04-graph-model.md`。
> 本文回答一个问题：**这套新设计要淘汰哪些现有内容**。分四类：直接删除 / 重写替代 / 保留 / 暂不淘汰。
> 现状盘点基于目录与关键文件实测（2026-02 快照），标注"待核实"的条目需要调用点检查后才能动手。

---

## 0. 判定标准

一条现有代码被淘汰，必须满足其一：

1. **无调用者**（死路径）——由新设计替代后仍无人使用。
2. **概念被取代**——新设计里有唯一归属，旧的重复实现留着必然双写。
3. **职责被拆散**——旧文件同时承担多段职责（入库+判定+建会话），新设计已按段拆分。

反之，凡是**平台知识**（适配器解析/发送/动作）与**领域能力**（记忆/视觉/审阅/情绪/计费/MCP）都不淘汰，只改接入点。

---

## 1. 直接删除

| # | 对象 | 规模 | 为什么删 | 删除前置条件 |
|---|---|---|---|---|
| A1 | `Engine/AdapterEng/AdapterEngine.cs`、`AdapterHandle.cs`、`AdapterEngineSpawnCheck.cs` | ~4.3 KB | 出向"名义枢纽"：`RegisterAdapter` 无调用者、`OnOutgoingReady` 无订阅者；出向实际走 `ChannelAccessImpl → AdapterManager`。新设计出向唯一路径是 `sink.channel.send` + 路由 + `OutboundLedger` | `sink.*` 与路由可用（阶段 B/C） |
| A2 | `Engine/Messaging/MessageEngine.cs`、`MessageEngineSpawnCheck.cs` | ~6.1 KB | 入向 `AcceptIncoming` 无调用者（含唯一的 DB 去重实现，属死代码）；`DeliverToSession` 被"按游标读库 + Park 投递"取代 | Ingest 管道 + `PendingIndex` 可用（阶段 B） |
| A3 | `Engine/Worker/ExpressEngineSpawnCheck.cs` | 6.7 KB | 名为 SpawnCheck 实为总入口（入库→提取→权限→claim→孵化）；新设计拆为 Ingest（L0）与 Activation/图（L1） | Gate + 图运行时可用（阶段 B/G） |
| A4 | `Engine/Worker/ImpulseConfig.cs` | 1.7 KB | 冲动值已无消费者（仅 `ColdTimeoutSeconds` 被 express 回收使用）；阈值/衰减语义被动态图模块与路由取代 | express 回收逻辑迁到会话生命周期（阶段 G） |
| A5 | `Adapter/IChannelAwareAdapter.cs`（`OwnsChannel` 前缀匹配）、`IEngineAwareAdapter.cs` | ~0.9 KB | "靠 channel 名字猜适配器归属"由显式绑定 `Channels.AdapterId` 取代 | 绑定表 + 路由落地（阶段 B） |
| A6 | `Database/ChannelRepository.FindOrCreateAsync` | ~0.3 KB | 自动建频道路径；新设计频道必须显式 `attach` | `Channels.AutoCreateMode=manual` 生效（阶段 F） |
| A7 | `Engine/System/`（空目录） | — | 无内容 | 立即 |
| A8 | `WebUI/Providers/DreamProvider.cs` | 8.4 KB | 对应已删除的 `DreamEngine`，属残留 | 立即（先确认路由无引用） |
| A9 | `WebUI/Shell/ProviderRegistry.cs` + `DataSourceManager.cs` + `PageContext.cs` | ~19.5 KB | 由 `Api` 的 `capabilities` + `PageSpec` 渲染器取代 | Api v1 可用（阶段 E） |
| A10 | `WebUI/Providers/*`（其余 16 个，合计 ~325 KB） | 见 §1.1 | 每个巨型 Provider 手搓 UI，一坨的根源；由 `PageSpec` 的 8 类 block 取代 | 页面逐页对照完成（阶段 E） |

### 1.1 WebUI Provider 明细（待替代规模）

| Provider | 规模 | 新归属 |
|---|---|---|
`AdapterProvider` | 44.0 KB | `/adapters`（Table+Form，数据来自 `query.adapters`） |
`PluginsProvider` | 49.8 KB | `/plugins`（`query.plugins`；不再自行扫盘） |
`SessionsProvider` | 31.9 KB | `/dashboard` + `/sessions` |
`TestProvider` | 33.4 KB | `/activation/test`（`graph.test` + 试跑面板） |
`MemoryProvider` | 27.0 KB | `/memory`（query 驱动） |
`LogsProvider` | 20.6 KB | `/logs`（`Stream` block + 订阅） |
`ConfigProvider` | 19.5 KB | `/settings`（`SettingDescriptor.Schema` 自动生成） |
`VisionProvider` | 19.9 KB | `/vision`（query 驱动） |
`ConsoleProvider` | 18.6 KB | `/console`（command 化） |
`ChannelProvider` | 13.1 KB | `/channels`、`/channels/{ref}` |
`ReviewProvider` | 11.6 KB | `/review` |
`DashboardProvider` | 10.1 KB | `/dashboard` |
`CurrencyProvider` | 9.6 KB | `/currency` |
`EnginesProvider` + `EngineDetailProvider` | 14.9 KB | `/engines` |
`LogTraceProvider` | 0.8 KB | 合并进 `/logs` |

合计约 **333 KB** 的手写 UI 代码被 schema 化替代。

---

## 2. 重写替代（能力保留，实现废弃）

| 旧 | 规模 | 新形态 | 拆分要点 |
|---|---|---|---|
`Engine/Worker/SessionManager.cs` | 13.0 KB | Ingest + 会话上下文构建 | 拆出"用户/频道映射 + 入库 + DedupeKey"（L0）与"最近 N 条上下文"（L3） |
`Engine/Session/SessionEngine.cs` | 56.9 KB | Kernel/Session + 资源仲裁 | 保留 claim 语义；激活改为"先 claim 后建会话"；express 孵化消失 |
`Engine/Session/RuntimeSession.cs` | 71.6 KB | 段执行器 + 端口 | 保留工具循环/上下文组装；`_inputs` 端口与 `WaitForActivationAsync` 权重轮询废弃 |
`Engine/Session/SessionPersistence.cs` | 20.3 KB | 三样持久状态 | 对齐「游标 / 等待条目 / 图实例变量 / 模块状态」，其余不落盘 |
`Adapter/AdapterManager.cs` | 14.3 KB | Ingest 注册 + 路由 + 租约 | `ResolveByChannelId` 的猜名字逻辑废弃；保留启停与动作分发 |
`Tool/Host/ChannelAccessImpl.cs` | 16.3 KB | `sink.channel.send` + 台账 | 出向写 `OutboundLedger`；分段续发 |
`Database/MessageRepository.cs` | 10.4 KB | 分库仓储 | 按适配器分库；`DedupeKey`、`PendingIndex`、跨库 fan-out |
`Database/DbManager.cs` | 20.9 KB | 多库管理 | 保留建表/PRAGMA；加 `SchemaVersion` 与逐库迁移 |
`Engine/Command/CommandSpawnCheck.cs` | 7.8 KB | Gate 阶段的命令拦截 | 能力保留；接入点从 `RouteMessageAsync` 前移到 Ingest 之后 |
`Engine/Core/MasterEngine.cs` | 38.6 KB | 组合根 + 事件总线 | 保留 `EventBus`/`ISubEngine`/`Gate`；删掉入向串行化总闸 |
`WebUI/Services/ModelLogService.cs`+`LogTraceService.cs` | 21.7 KB | 日志订阅 | 由 `events` 订阅 + `Stream` block 提供 |
`Engine/Context/ChannelContextService.cs` | 24.8 KB | 查询 + 游标 | 保留上下文组装，数据来源改为"库 + 游标" |

> 说明：这些是**重写**而非删除——能力（claim、工具循环、上下文组装、日志）全部保留，只是换了承载者。

---

## 3. 保留（资产，仅补接口）

| 类别 | 对象 | 处理 |
|---|---|---|
适配器实现 | `Adapter/OneBot/*`、`Icalingua/*`、`Http/*`、`File/*`（parser、protocol、forward renderer、actions） | 保留；补 `AdapterCapabilities`、`IAdapterQuery`、`DataPolicy`、保留原始载荷 |
适配器契约 | `IAdapter.cs`、`IncomingMessage`、`OutgoingMessage`、`MessageSegment`、`MessageAttachment`、`AdapterAction`、`ImageStorage` | 保留；`IncomingMessage` 扩展为 `InboundEvent` |
模型内核 | `Core/*`（`AgentCore`、`CoreBase`、`ExtractionCore`、`Processor`、`NativeToolCallHandler`） | 保留 |
组件机制 | `Component/*`（`ComponentHost`、`GlobalComponentHost`、`ComponentRegistry`、`ToolListFormatter`） | 保留；纳入统一 `IContributionRegistry` |
工具 | `Tool/Core/*`（Compact/ConnectChannel/EndSession/Escalate/SwitchCore…） | 保留；改走新接口 |
领域能力 | `Engine/Mind/*`、`Engine/Vision/*`、`Engine/Timer/*`、`Engine/Context/*` | **不淘汰**，仅接新事件/查询 |
数据实体与仓储 | `Database/*`（Claim、Journal、Person、User、Image、ReviewQueue、EmotionSnapshot、Keyword、FrameworkEvent 等） | 保留 |
插件 | `Plugins/*`（24 个） | 保留；整理 manifest、去宿主依赖（见 §5） |
基础设施 | `MCP/*`、`Logging/*`、`Util/*`、`Config/PathConfig|PromptLoader|TemplateReleaser` | 保留 |
测试 | `AgentCoreProcessor.Tests/*` | 保留；受影响用例随实现重写 |

---

## 4. 暂不淘汰（明确划界，防误删）

以下能力**不在**本次替代范围，任何"重构"都不应以它们为代价：

- 记忆/提取：`MindEngine`、`ExpressExtractor`、`ExtractService`、`RetrieveService`、`Writer`、`EvidenceEngine`、`Resurrector`
- 视觉：`VisionEngine`、`PhaseConfig`
- 审阅：`ReviewQueueService`、`ReviewStage`、`ReviewSessionIncubator`
- 情绪/冲突：`EmotionService`、`ConflictService`
- 计费：`CurrencyServiceImpl`、`CurrencyStore`
- 命令体系：`Command/*`（19 个命令）
- 子代理、MCP、图像存储
- `Engine/Worker` 的事件循环基础：`ILoopBus`、`LoopEvents`、`LoopGate`、`EngineModule`、`ParticipantInfo`、`SessionContext`、`BotOutputParser`（待核实内部耦合后决定是否随 RuntimeSession 重写一起动）

---

## 5. 待核实清单（动手前必须查调用点）

| 对象 | 疑点 | 检查方式 |
|---|---|---|
`Engine/Worker/ChannelStateManager.cs`、`ChannelConfig.cs`、`SpeakGuard.cs` | 是否被"路由 + claim"完全取代 | 全局搜索引用；确认无调用者后删 |
`Engine/Messaging/MessageTypes.cs` | `EngineIncomingMessage` 是否仍被 `SessionManager`/端口使用 | 搜索类型引用 |
`Engine/Core/IInjectProvider.cs`、`Component/SubAgentAccessAdapter.cs` | 是否只是为"插件直连宿主"搭的桥 | 搜索实现者；确认 `Plugin.WorkingTools` 改造后无用 |
`Tool/Host/*AccessImpl.cs`（ReviewStage/ReviewQueue/Writer/Emotion/Product/Retrieve） | 是否仍被工具或组件使用 | 搜索 `GetService<` 调用 |
`Config/SetupWizard.cs` | 首启流程是否仍需要 | 搜索入口调用 |
`Client/`、`LilaraConsoleClient/`、`PlaywrightDemo/`、`tools/` | 是否随 Api v1 迁移或废弃 | 看 README/脚本引用 |

建议做法：每个候选先做一次"零引用"确认，再删除；不要靠"看起来没用"。

---

## 6. 淘汰顺序（有依赖，不能并行乱删）

```
阶段 B（新 Kernel 骨架）
   └─ 先能跑：Ingest → 分库写 → 查询 fan-out
        ↓ 才允许删 A5、A6（绑定/自动建频道）
阶段 C（适配器接新管道）
   └─ 适配器同时喂新旧两个消费者（shadow）
        ↓ 才允许删 A1、A2、A3（旧引擎半成品）
阶段 D（插件整理）
   └─ 统一贡献注册 + 去宿主依赖
        ↓ 才允许删 §5 里的 IInjectProvider/SubAgentAccessAdapter 桥
阶段 E（WebUI 切出）
   └─ Api v1 + PageSpec 渲染器可用，页面逐页对照
        ↓ 才允许删 A9、A10（Provider 巨石，约 333 KB）
阶段 F（清债）
   └─ 删 A4、A7、A8；重写 §2 中剩余的旧实现
```

**关键约束**：`WebUI/Providers/*` 必须在 Api 完全覆盖其功能后才删——它们是当前唯一能观测系统状态的面板，先删等于自断观测。

---

## 7. 规模估算

| 类别 | 规模 | 说明 |
|---|---|---|
直接删除 | ~350 KB | 其中 WebUI Provider 占 ~333 KB |
重写替代 | ~300 KB | Session/RuntimeSession/MasterEngine/Manager 等 |
保留补接口 | 主体代码 | 适配器、Core、Component、Mind/Vision、插件 |

删除与重写的合计约 **650 KB 级**，与"适配器与插件是资产、内核与 WebUI 是负债"的判断一致。

---

## 7.1 旧数据处置：不迁移

按 `00-decisions.md` D40，重构**不做数据迁移**：

| 对象 | 处置 |
|---|---|
旧库（消息/频道/会话/记忆索引） | 弃用，必要时保留一份冷备份后删除；新库从空白开始 |
频道与适配器绑定 | 在 WebUI 重新登记（`attach`），不靠脚本回填 |
旧会话与运行态 | 不恢复；重启即空白 |
旧插件数据目录 | 保留（插件自有格式，不属内核 schema） |

好处：省掉"旧形状 → 新形状"的全部转换代码与验证成本；代价是历史对话不可检索——这是明确接受的取舍。
`--test` 夹具因此必须在 bootstrap 里显式 attach `file` 频道，不能依赖旧数据。

---

## 8. 风险

| 风险 | 缓解 |
|---|---|
删早了失去观测能力 | 严格按 §6 顺序；每个删除先跑"零引用"确认 |
双写期数据不一致 | shadow 模式下新旧都写，比对差异后再切 |
`MessageEngine` 的 DB 去重是死代码但**唯一的去重实现** | 删除前先把去重逻辑搬到 Ingest（阶段 B），否则会丢能力 |
`ChannelAccessImpl` 被测试大量覆盖 | 重写时同步迁移测试，不是删测试 |
Provider 删除影响插件 UI | 插件 UI 改 `ui/*.json` 贡献，随阶段 D/E 一起迁移 |
