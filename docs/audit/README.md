# 代码审计 — 2026-05-26

总代码量 ~35,000 行 C#，分 10 个模块逐项检查。

每模块关注点：
- 线程安全 / 竞态条件
- 异常处理缺口
- 资源泄漏（Task/CancellationToken/Dispose）
- 配置与代码不一致
- 最近改动引入的回归风险
- 关键位置注释缺失（非显而易见的约束/变通/边界条件是否有说明）
- 逻辑过于复杂（尤其是为单一功能开了特例的路径，是否可简化）
- 耦合过紧（是否应该解耦到独立组件/插件，依赖方向是否合理）

---

## 审计进度

| # | 模块 | 文件数 | 状态 |
|---|------|--------|------|
| 1 | 引擎核心 (Engine Core) | ~15 | ✅ 已完成 → [报告](module-01-engine-core.md) |
| 2 | 频道循环 (Channel Loop) | ~15 | ✅ 已完成 → [报告](module-02-channel-loop.md) |
| 3 | 系统循环+子Agent (System Loop) | ~10 | ✅ 已完成 → [报告](module-03-system-loop.md) |
| 4 | 做梦+复盘 (Dream & Review) | ~12 | ✅ 已完成 → [报告](module-04-dream-review.md) |
| 5 | 记忆系统 (Memory) | ~16 | ✅ 已完成 → [报告](module-05-memory.md) |
| 6 | 插件+工具系统 (Plugins & Tools) | ~55 | ✅ 已完成 → [报告](module-06-plugins-tools.md) |
| 7 | WebUI | ~50 | ✅ 已完成 → [报告](module-07-webui.md) |
| 8 | 数据库层 (Database) | ~25 | ✅ 已完成 → [报告](module-08-database.md) |
| 9 | 客户端+Core处理 (Clients & Cores) | ~25 | ✅ 已完成 → [报告](module-09-clients-cores.md) |
| 10 | 适配器+基础设施 (Adapters & Infra) | ~15 | ✅ 已完成 → [报告](module-10-adapters-infra.md) |

## 模块文件清单

### 1. 引擎核心
- MasterEngine.cs (740行)
- Gate.cs, Agent.cs, AgentConfig.cs
- EventBus.cs, ModuleBus.cs
- CrossRequest.cs, CrossRequestRegistry.cs, DelegationBus.cs
- ISubEngine.cs, IAgentSession.cs, IAgentHost.cs, IEngineLifecycle.cs, IInjectProvider.cs
- SessionManager.cs, LoopId.cs, ChannelSignal.cs, EngineEvent.cs
- SignalFilterConfig.cs, SignalCategory.cs, SleepState.cs
- TrustProgressionConfig.cs

### 2. 频道循环
- ChannelEngine.cs (1844行，最大文件)
- ChannelEngineSpawnCheck.cs
- ImpulseTracker.cs, ImpulseConfig.cs
- ChannelStateManager.cs, ChannelConfig.cs
- ChannelContextPersistence.cs
- LoopGate.cs, ILoopBus.cs, LoopEvents.cs
- SessionContext.cs, ParticipantInfo.cs
- BotOutputParser.cs, EngineModule.cs
- AlertHandler.cs
- ChannelExtractionWorker.cs
- Modules/LoopControlModule.cs

### 3. 系统循环+子Agent
- SystemEngine.cs (1040行)
- SystemEngineSpawnCheck.cs
- TaskSession.cs
- ContextPersistence.cs, ChannelContextPersistence.cs
- ContextCompressionModule.cs, CompressionTierModule.cs
- PendingEventsModule.cs
- Command/CommandSpawnCheck.cs

### 4. 做梦+复盘
- DreamEngine.cs (1234行), DreamEngineSpawnCheck.cs
- DreamScheduler.cs, DreamHistory.cs, DreamConfig.cs, DreamStats.cs
- ReviewEngine.cs (692行), ReviewConfig.cs, ReviewProgress.cs
- EvaluationEngine.cs
- SleepTalkCore.cs
- SleepState.cs

### 5. 记忆系统
- MemoryService.cs
- Memory.cs, MemoryRepository.cs
- MemoryLink.cs, MemoryLinkRepository.cs
- MemoryExtractionCore.cs, MemoryQueryCore.cs
- DedupCore.cs, WeightCore.cs, LinkCore.cs
- CombineCore.cs, ConsolidationCore.cs, ConsolidationFinalCore.cs
- PersonaMemoryEntry.cs, PersonaMemoryRepository.cs
- TempMemoryEntry.cs, TempMemoryRepository.cs
- MemoryType.cs

### 6. 插件+工具系统
- PluginLoader.cs, ToolRegistry.cs, ToolExecutor.cs, TypeForwards.cs
- ComponentHost.cs, ComponentRegistry.cs, ComponentConfig.cs
- GlobalComponentContext.cs, LoopComponentContext.cs
- GlobalComponentHost.cs, SimpleServiceProvider.cs
- AgentMessagingImpl.cs, SubAgentAccessAdapter.cs
- ToolListFormatter.cs
- PluginSDK/ (所有接口 + 数据模型)
- Plugins/Plugin.BasicTools/ (speak, send_media)
- Plugins/Plugin.WorkingTools/ (pinboard, thinking_notes, retain_list, mark_for_review, task_list)
- Plugins/Plugin.MemoryTools/ (memory)
- Plugins/Plugin.FileTools/ (read/write/list/move/delete/copy)
- Plugins/Plugin.CrossLoopTools/ (10 个跨循环工具)
- Plugins/Plugin.SystemTools/ (create/stop sub agent, send instruction, list)
- Plugins/Plugin.ReviewTools/ (15 个复盘工具)
- Plugins/Plugin.ScheduledTasks/ (schedule/list/cancel)
- Tool/Host/ (BeaconAccessImpl, ChannelAccessImpl, MemoryAccessImpl, PersonAccessImpl, ReviewAccessImpl, ReviewControlImpl, ToolContextImpl)
- Tool/Core/ (CoreTools, CompressTool, DeescalateTool, EscalateTool)

### 7. WebUI
- Components/Pages/ (Dashboard, Login, LogTrace, McpStatus, Memory_Graph)
- Components/Cards/ (8 种卡片: Action/Chat/Detail/Form/PropertyEditor/Status/Stream/Table/Tree)
- Components/Layout/ (MainLayout, LoginLayout, NavMenu, NavSection)
- Components/Shell/ (CardGrid, CardHost, DynamicPage)
- Components/Shared/ (AlertPanel, DataTable, StatCard, RedirectToLogin)
- Shell/ (ProviderRegistry, DataSourceManager, PageContext)
- Services/ (AlertService, SystemMonitor, LogStreamService, LogTraceService, ModelLogService, TokenStatsService, WebAuthService, WebConfig, 各种Snapshot)
- Services/Alerts/ (EngineAlertProvider, MemoryAlertProvider)
- Providers/ (17 个 Provider)
- Navigation/ (NavConfig, NavItem)
- wwwroot/ (app.css, log-trace.css, log-trace.js, memory-graph.js)

### 8. 数据库层
- DbManager.cs
- Channel.cs, ChannelRepository.cs
- Person.cs, PersonRepository.cs, User.cs, UserRepository.cs
- UserMessage.cs, MessageRepository.cs
- Memory.cs, MemoryRepository.cs
- MemoryLink.cs, MemoryLinkRepository.cs
- DreamLog.cs, DreamLogRepository.cs
- ImageRecord.cs, ImageRepository.cs
- ModelCallLog.cs, ModelCallLogRepository.cs
- PersonaMemoryEntry.cs, PersonaMemoryRepository.cs
- TempMemoryEntry.cs, TempMemoryRepository.cs
- ReviewHint.cs, ReviewHintRepository.cs
- ReviewLog.cs, ReviewLogRepository.cs
- EvaluationScore.cs, EvaluationScoreRepository.cs
- MemoryType.cs, PermissionLevel.cs

### 9. 客户端+Core处理
- ClaudeModelClient.cs (521行), OpenAIModelClient.cs (521行)
- ModelClientBase.cs, ModelClientFactory.cs, ApiClientCfg.cs
- IModelClient.cs, IEmbeddingProvider.cs, IVisionProvider.cs, IOcrProvider.cs
- SiliconFlowEmbeddingProvider.cs, SiliconFlowVisionProvider.cs, SiliconFlowOcrProvider.cs
- AgentCore.cs, CoreBase.cs (497行)
- Processor.cs, PromptBuilder.cs
- ModelOutput.cs
- SummarizationCore.cs, ReviewCore.cs
- ConsolidationCore.cs, ConsolidationFinalCore.cs
- PreprocessingCore.cs
- ExpressToolCallHandler.cs, NativeToolCallHandler.cs

### 10. 适配器+基础设施
- IAdapter.cs, AdapterManager.cs, AdapterFactory.cs
- AdapterAction.cs, AdapterStatus.cs, AdapterInstanceConfig.cs
- ConsoleAdapter.cs
- OneBot/OneBotAdapter.cs, OneBotActions.cs, OneBotConfig.cs, OneBotMessageParser.cs
- File/FileAdapter.cs
- ImageStorage.cs (490行)
- IncomingMessage.cs, OutgoingMessage.cs, MessageAttachment.cs
- MCP/McpServerManager.cs, McpBridgeTool.cs, McpConfig.cs, McpServerConnection.cs
- Program.cs
- Config/PathConfig.cs, SetupWizard.cs, TemplateReleaser.cs
- Command/ (17 个命令)
- Util/TextUtil.cs, VectorUtil.cs
