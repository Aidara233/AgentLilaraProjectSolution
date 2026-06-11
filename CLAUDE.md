# Agent Lilara — AI 协作指引

## 项目状态

**核心特性**：
- 双循环架构：频道循环（用户交互）+ 系统循环（任务调度）
- 系统循环不直接发消息：一律通过 NotifyChannel 注入频道循环，由频道循环自行决定回应方式
- 委托系统：频道循环提交委托 → 系统循环评估(accept/queue/reject) → 子agent执行 → 结果回传频道循环
- TaskBridge 异步通信：任务队列 + 通知队列
- 子agent系统：TaskSession 被动执行，工具白名单，禁止套娃，支持 delegationId 自动回写结果
- 引擎容错：API 调用失败不杀循环，连续失败 exponential backoff，SystemEngine 崩溃后 SpawnCheck 自动重启
- 优雅退出：ApplicationStopping 钩子 → 停适配器 → 停引擎（signal gate + CTS）→ 等 30s → 强杀兜底 → close startupSignal
- 引擎生命周期信号：每个引擎 RunAsync 用 Signal.Continue 创建 lifecycle span（scope=自己），退出时 Close 带 reason
- 上下文持久化：SystemContext.json WAL 模式
- 上下文压缩：CompressionTierModule 三层压缩（L1提示/L2提醒/L3硬保底），模型可调 compress 工具主动压缩
- 关注列表：系统循环下发规则，频道循环语义匹配
- 睡觉系统：DreamEngine 常驻循环（秩序+巡逻），由 force-sleep 信号触发启动，小睡/大睡通过信号展开
- 睡眠打断分级：走神被@醒、小睡需@+关键词叫醒（仅@触发梦话）、大睡仅 force-wake 信号可唤醒
- 睡眠期消息拦截：ChannelEngineSpawnCheck 按 CurrentSleepState 拦截，消息入库但不响应，醒来后自动补提取记忆
- Prompt Caching：Claude 系 Core 启用 promptCaching，中转站已验证兼容
- Token 统计：ModelCallLog 数据库表记录每次调用（含 usage + caller tag）。WebUI 已有 LogsProvider（`/p/logs/tokens` 和 `/p/logs/model`）提供可视化。
  - CallerTag 标识调用来源：Channel:{id} / System / SubAgent:{sessionId} / Review:{mode} / Dream:{phase}
- 工具管理：ToolRegistry 动态注册/卸载 + WebUI /p/plugins 管理
- **模式系统（ModeConfig）**：数据驱动的模式管理，`Storage/Engine/ModeConfig.json` 定义所有模式，WebUI /p/modes 可视化编辑
  - **Express**（轻量对话）：fire-and-forget，speak/wait/poke/escalate，maxRounds=8
  - **plan**（规划）：只读分析，5轮，不能修改文件
  - **browse**（浏览）：阅读搜索，10轮，不能修改
  - **build**（构建）：编写代码，15轮，可读写文件，不能创建子agent
  - **manage**（管理）：调度委托、管理远程，toolDefaults=enabled
  - **fullpower**（完全权限）：不受限，30轮，切换前须征得用户明确同意
  - Express→Working：escalate 工具（默认进入 plan），上下文清空
  - Working 子模式间：switch_mode 工具横向切换，保留上下文，**必须先征求用户同意**
  - Working→Express：deescalate 工具，清空上下文
  - 工具过滤：ModeConfigLoader.IsToolEnabled(modeId, toolName) — 组件开关(ComponentConfig) 和模式工具列表(ModeConfig) 两层独立
  - AgentCore.CurrentModeId 统一 Express/Working 过滤路径
- 消息段交错：MessageSegment（Text/Image/At/Reply）按原文顺序排列。OutgoingMessage.Segments 优先于旧 Content+Attachments 路径。OneBotActions 直接消费 Segments 构建分段消息数组。
- 图片发送：本地图片通过 base64:// 编码传给 NapCat（不依赖 file:/// 或裸路径）。
- 多模态图片处理：ContentPart 支持 text/image(path或base64)、图片感知（img 标记+直传/描述分流）、ViewImageTool Working专用、SkiaSharp 缩略图

## 工作规范

- 遇到 claude code 本身异常时，如果出错的是基础工具，立即向用户报告，不要头铁硬试
- fetch 工具通常不可用，获取资料时可能只能通过 web search 的摘要拼凑信息。如果确实需要下载文件，可以要求用户协助
- 如果项目不太顺利，可以停下来找用户协商解决方法，除非你觉得你的方法很合理，否则另辟蹊径之前必须获得用户支持

## 冷启动

如果你刚进入对话或经历了上下文压缩，按以下顺序恢复上下文：

1. 读取 `docs/architecture-map.md` — 架构全景图，一页纸恢复基本认知
2. 检查 memory 索引 `MEMORY.md` 了解记忆文件全景，读 `project_progress.md` 了解当前进度
3. 需要了解特定设计决策时，按需读取对应的 memory 文件（见下方"记忆文件索引"）
4. Storage 目录**不在**解决方案文件夹下，也不在 git 仓库中。

不要一次性读取所有源代码文件。按需读取，用到哪个读哪个。

## 记忆文件索引

Memory 目录：`~/.claude/projects/E--Workspace-AgentLilaraProject/memory/`，入口索引为 `MEMORY.md`。

**项目设计：**
- `project_overview.md` — 项目概览，快速入口指针
- `project_progress.md` — 开发进度 + 待办（每轮必读）
- `project_engine_unification.md` — 引擎循环统一设计（Gate+Agent+堆叠上下文）
- `project_system_loop.md` — 系统循环纯调度者定位 + 子agent体系
- `project_loop_redesign.md` — 闸门模型 + 模块分层（已实现）
- `project_dream_design.md` — 做梦机制：片段类型、数据域、睡眠分级
- `project_dream_talk.md` — 梦话生成 + 睡眠打断分级
- `project_memory_system.md` — 多维标签存储 + 检索设计（已被记忆重构取代）
- `project_memory_redesign.md` — 存储/检索/提取全面重构
- `project_tool_design.md` — 一切皆工具 + Agent 循环（已被插件化方案取代）
- `project_tool_plugin_refactor.md` — Contract 接口 + PluginLoader + 插件化架构（已被引擎统一化方案取代）
- `project_social_engagement.md` — 冲动值 + 提及检测 + 社交网关
- `project_impulse_redesign.md` — 冲动值负反馈重设计
- `project_user_system.md` — Person/User 双层模型 + 信任/权限分离
- `project_webui.md` — WebUI 管理面板（卡片系统 Phase 1+2）
- `project_webui_redesign.md` — 导航重排 + Shell/Provider 分层
- `project_webui_context_viewer.md` — 频道详情上下文查看器（待开发）
- `project_logging_redesign.md` — Signal 信号追踪模型 + 单表 events
- `project_log_page_design.md` — 日志页 git-tree 风格 SVG 可视化
- `project_signal_instrumentation.md` — Signal 埋点扩展模板
- `project_expression_issues.md` — 模型编造经历问题 + Persona 约束
- `project_qq_adapter.md` — OneBotAdapter QQ 平台接入
- `project_channel_id_risk.md` — ChannelId 碰撞风险（暂缓）
- `project_claude_proxy_issue.md` — 中转站兼容性问题
- `project_future_plans.md` — 未来计划备忘
- `project_worker_redesign.md` — WorkerEngine 设计（已被系统循环方案取代）

**协作偏好：**
- `feedback_collab.md` — 碰上问题先商量，不要过度拆分代码
- `feedback_workflow.md` — 大型改动后主动 commit + 试运行
- `feedback_webui_layout.md` — 复杂卡片用 flex Sidebar，避免 CSS Grid 行共享

**参考信息：**
- `reference_napcat.md` — NapCat QQ 协议端路径
- `reference_webui_login.md` — WebUI 登录凭据
- `reference_design_notes.md` — 用户设计灵感目录

## 文档索引

项目文档集中在 `docs/`，以下是完整索引：

**架构：**
- `docs/architecture-map.md` — **架构全景图**（引擎生态/消息流/做梦/记忆/工具/WebUI/日志/MCP），冷启动第一站
- `docs/review-engine-redesign.md` — ReviewEngine 自由探索模式重设计

**Signal 日志系统（`docs/signal/`）：**
- `docs/signal/signal-logging-guide.md` — Signal API 快速参考（Begin/Continue/Open/Close/Event/Error）
- `docs/signal/signal-instrumentation-guide.md` — 埋点指南：在哪放日志、怎么放（给子 agent 用）
- `docs/signal/signal-channel-template.md` — 引擎循环埋点参考模板（已验证模式）

**插件系统（`docs/plugins/`）：**
- `docs/plugins/README.md` — 概念概览 + 运行时机制 + 完整插件清单 + 设计决策
- `docs/plugins/claude-quick-ref.md` — **新 Claude 会话速查**：插件清单、开发套路、常见坑、关键文件路径（冷启动优先读）
- `docs/plugins/path-config.md` — **路径配置指南**：Storage vs BaseDirectory、插件数据路径、paths.json、常见路径错误
- `docs/plugins/patterns.md` — **实现模式**：Static Accessor Bridge、异步通知 Drain、Global Timer、文件沙箱 + 其他模式速查
- `docs/plugins/quickstart.md` — 从零创建插件，含 Global/Loop 组件完整代码示例
- `docs/plugins/api-reference.md` — 所有接口、属性、枚举、服务、数据类型的完整参考

**Core 系统（`docs/core/`）：**
- `docs/core/README.md` — 文件清单 + 分类索引
- `docs/core/infrastructure.md` — CoreBase / Processor / ModelOutput 基类和数据模型
- `docs/core/agent-core.md` — AgentCore 统一对话核心（Express + Working 双模式）
- `docs/core/memory-cores.md` — 7 个记忆处理核心（提取/去重/权重/关联/组合/整合）
- `docs/core/context-management.md` — SummarizationCore 三层压缩
- `docs/core/special-purpose.md` — SleepTalkCore / PreprocessingCore
- `docs/core/stream-handlers.md` — NativeToolCallHandler / ExpressToolCallHandler
- `docs/core/configuration.md` — JSON 配置文件格式、协议选择、字段说明

## 项目语言

- 代码注释、文档、讨论：中文
- commit message：英文
- 类名/方法名/变量名：英文

## 编码约定

- .NET 8, C#, SQLite-net-pcl, Newtonsoft.Json
- 新引擎：实现 ISubEngine，通过 SpawnCheck 注册或 ctx.StartEngine() 直接启动
- 新工具：实现 ITool（AgentLilara.PluginSDK），放在 Plugins/ 下独立项目，引用 SDK 编译为 DLL。参数名必须英文（Bedrock 代理限制）。文件操作类工具继承 `FileToolBase`（`FileToolKit.Shared`）统一沙箱和快捷方法。
- 插件必须包含 `plugin.json` 清单文件（id/entry/version/components），`CopyToHostPlugins` MSBuild 目标自动输出到 `Plugins/{$(MSBuildProjectName)}/` 子目录。
- 插件共享代码：如需跨插件复用，抽到独立类库项目（如 `FileToolKit.Shared/`），不要用链接文件或复制粘贴。类库引用用 `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` 确保依赖 DLL 被复制到 Plugins 输出目录。
- Workspace 路径：插件内需要 Workspace/ 目录时，使用 `IPluginStorage.WorkspaceDirectory`，不要通过 `GlobalDirectory` 做 `..` 相对跳转。
- 新 Core：继承 CoreBase，配置放 Storage/Core/{CoreName}.json
- 新实体：放 Database/，Repository 模式，DbManager.InitAsync() 加 CreateTableAsync
- 基础设施引擎：设 IsInfrastructure => true（不影响 IsIdle 判定）

## 工作流（硬性要求）

我们更习惯先确定绝大多数问题后再动手，因此不必太急着修改代码，先确认讨论完成后再着手修改，以保证思路正确。

在进行编码或解决问题时，除非用户明确要求全部自主完成，否则在考虑任何替代解决方案前，都应当征求用户意见。

如果发生了意外错误，也同样可以考虑寻求用户意见，部分问题交给人类协助完成会更有利于工作效率，硬着头皮干反而效率更低。

考虑问题时需要一并注意以下可能关联的部分：
- 代码本身或其他代码，尤其是各种引擎
- 数据库相关
- 配置文件相关
- webui 相关
- 文档相关

每轮代码改动完成后，必须立即执行：
1. 编译：先杀进程再 build 再启动，一气呵成避免 exe 锁定问题
   ```bash
   cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null; dotnet build && dotnet run
   ```
   注意：Git Bash 会把 `/IM` `/F` 等斜杠参数误解为路径，必须用 `cmd //c "..."` 包裹 taskkill 命令。
   杀进程不会造成数据损坏（SQLite WAL 模式 + 配置文件原子写入）。
2. `git commit` 提交改动，仓库在 solution 内而非工作目录内
3. `--test` 模式试运行验证（需要模拟对话节奏时加 `--delay N`）
4. 更新文档，方便下次冷启动
  - 文档主要包括`E:\Workspace\AgentLilaraProject\AgentLilaraProjectSolution\docs`下的文档，应当尽可能让所有文档都集中在这里面，方便管理。
  - 可以顺带把旧计划文档清空，方便下次填写。
  - 以及这个文档——`CLAUDE.md`本身，很多内容可能不需要更改，但如有必要（比如用户多次提醒过某件事、出现了新的技术需要明确约定）可以加进来，防止遗忘。

不要等用户提醒，做完改动就走这几步。

## 关键路径

- 入口：Program.cs（--file / --debug / --test / 默认 Web 服务器）
- 数据库：Database/DbManager.cs（初始化 + 迁移）+ 13 张业务表 + logs.db
- 会话管理：Engine/Core/SessionManager.cs（用户映射 + 频道映射 + 消息入库）
- 引擎内核：Engine/MasterEngine.cs（SpawnCheck 注册表 + 活跃引擎表 + 全局组件宿主）
- 引擎孵化：Engine/Core/ChannelEngineSpawnCheck.cs（频道引擎生命周期 + 睡眠拦截）
- 频道循环：Engine/Worker/ChannelEngine.cs
- 系统循环：Engine/System/SystemEngine.cs
- 跨循环请求：Engine/Core/CrossRequestRegistry.cs（JSONL 持久化 + 状态机）
- 委托总线：Engine/Core/DelegationBus.cs（定向路由，非全局广播）
- 统一通信：Engine/Core/LoopId.cs + Engine/Core/CrossRequest.cs（请求模型）
- 通信实现：Component/AgentMessagingImpl.cs（IAgentMessaging 实现）
- 子agent会话：Engine/System/TaskSession.cs
- 引擎会话接口：Engine/Core/IAgentSession.cs（TaskSession 统一抽象）
- 循环闸门：Engine/Core/Gate.cs（delegate 驱动，组合不继承）+ Engine/Worker/LoopGate.cs（auto-reset 信号）
- 模式配置：templates/Engine/ModeConfig.json（模板）+ Storage/Engine/ModeConfig.json（运行时）
- 模式加载：Engine/ModeConfigLoader.cs（Load/Save/IsToolEnabled/GetMode）
- 模式切换工具：Tool/Core/SwitchModeTool.cs（Working 子模式横向切换，需用户确认）
- Escalate/Deescalate：Tool/Core/EscalateTool.cs + Tool/Core/DeescalateTool.cs
- 模式 WebUI：WebUI/Providers/ModesProvider.cs（/p/modes 页面）
- Agent 循环：Engine/Core/Agent.cs（多轮推理，退避策略）
- Agent 宿主接口：Engine/Core/IAgentHost.cs（BuildStartInjectAsync/BuildRoundInjectAsync）
- Agent 配置：Engine/Core/AgentConfig.cs（轮次/退避/压缩阈值）
- 组件系统：Engine/Core/ComponentHost.cs（实例管理 + ModuleBus 订阅 + 工具注册）
- 组件注册：Engine/Core/ComponentRegistry.cs（类型注册，PluginLoader 扫描）
- 全局组件：Engine/Core/IGlobalComponent.cs / ILoopComponent.cs
- 三层压缩：Engine/Modules/CompressionTierModule.cs（L1提示/L2提醒/L3硬保底）
- 压缩工具：Tool/Core/CompressTool.cs（模型可调用，所有模式可用）
- 注入接口：Engine/Core/IInjectProvider.cs（BuildStartInjectAsync/BuildRoundInjectAsync）
- 生命周期：Engine/Core/IEngineLifecycle.cs（OnInitializeAsync/OnShutdownAsync）
- 信号类型：Engine/Core/ChannelSignal.cs（NewMessageSignal/BusEventSignal/CompressionSignal/ModeSwitchSignal）
- 模块总线：Engine/Core/ModuleBus.cs（每引擎独立 pub/sub）
- 频道持久化：Engine/Worker/ChannelContextPersistence.cs（per-channel JSON 原子写入）
- 系统持久化：Engine/System/ContextPersistence.cs（SystemContext.json WAL 模式）
- 统一模型调用：Core/AgentCore.cs（Express/Working 分流 + 原生 tool_use）
- 记忆检索：Memory/MemoryService.cs（RecallAsync：全量扫描 + 向量精排 + 关联扩展）
- 记忆提取：Core/MemoryExtractionCore.cs（每3条触发，独立 Worker）
- Embedding：Client/EmbeddingProvider.cs（bge-large-zh-v1.5，SiliconFlow）
- 维护引擎触发：Engine/Dream/DreamEngineSpawnCheck.cs（仅响应 force-sleep 信号）
- 维护引擎：Engine/Dream/DreamEngine.cs（常驻循环：秩序temp入库+巡逻衰减维护+三角闭合）
- 记忆热度模型：TempMemoryEntry.Heat（目标逼近，被召回滚雪球，冷掉入库）
- 梦话生成：Core/SleepTalkCore.cs（被@时无关键词触发）
- 复盘引擎：Engine/Dream/ReviewEngine.cs（自由探索模式，游标浏览，评价缓冲）
- 复盘配置：Engine/Dream/ReviewConfig.cs + Storage/Dream/ReviewConfig.json
- 评价引擎：Engine/Dream/EvaluationEngine.cs（边界阻力公式，session取平均）
- 复盘进度：Engine/Dream/ReviewProgress.cs + Storage/Dream/ReviewProgress.json
- 复盘工具：Plugins/Plugin.ReviewTools/（15个工具，review-tools 组件）
- 复盘SDK：AgentLilara.PluginSDK/Services/IReviewAccess.cs + IBeaconAccess.cs + IReviewControl.cs
- 复盘宿主：Tool/Host/ReviewAccessImpl.cs + BeaconAccessImpl.cs + ReviewControlImpl.cs
- 评价存储：Database/EvaluationScore.cs + EvaluationScoreRepository.cs
- 复盘日志：Database/ReviewLog.cs + ReviewLogRepository.cs
- 睡眠状态：Engine/Core/SleepState.cs
- 冲动值决策：Engine/Core/ImpulseDecider.cs + Storage/Engine/ImpulseConfig.json
- 视觉引擎：Engine/Vision/VisionEngine.cs（图片描述+OCR调度）
- 视觉模型：Client/SiliconFlowVisionProvider.cs + Storage/Core/VisionProvider.json
- OCR模型：Client/SiliconFlowOcrProvider.cs + Storage/Core/OcrProvider.json
- 图片存储：Adapter/ImageStorage.cs
- Token 统计：Database/ModelCallLog.cs + ModelCallLogRepository.cs
- 信号系统：Logging/Signal.cs（静态门面）+ Logging/LogDatabase.cs（独立 SQLite）+ Logging/LogWriter.cs（批量写入）
- 信号查询：Logging/LogQuery.cs + Tool/Host/LogAccessImpl.cs（ILogAccess 桥接）
- 工具管理：Tool/ToolRegistry.cs（注册/卸载/禁用）+ Tool/Host/ToolProfileManager.cs（链式继承）
- 核心工具：Tool/Core/CoreTools.cs（wait + continue_loop）+ Tool/Core/EscalateTool.cs
- 插件 SDK：AgentLilara.PluginSDK/（共享契约：ITool/IInjectProvider/服务接口/CardSchema）
- 插件加载：Tool/Host/PluginLoader.cs（AssemblyLoadContext 隔离，多类型发现 + 构造注入）
- 记忆桥接：Tool/Host/MemoryAccessImpl.cs（IMemoryAccess → Repository + Embedding）
- 适配器接口：Adapter/IAdapter.cs + AdapterManager.cs + AdapterFactory.cs
- OneBot 适配器：Adapter/OneBot/OneBotAdapter.cs（WS连接 + 消息映射 + QQ协议）
- File 适配器：Adapter/File/FileAdapter.cs（文件轮询，测试用）
- MCP 系统：MCP/McpServerManager.cs + McpBridgeTool.cs + McpConfig.cs
- WebUI Shell：WebUI/ProviderRegistry.cs + DynamicPage.razor + CardGrid.razor + CardHost.razor
- WebUI 数据层：WebUI/DataSourceManager.cs（Fetch重试 + Subscribe推送 + 路由参数注入）
- WebUI 卡片类型：WebUI/Cards/（TableCard/StatusCard/FormCard/StreamCard/ChatCard/ActionCard/TreeCard）
- WebUI Provider：WebUI/Providers/（Dashboard/Engines/EngineDetail/Channel/Dream/Review/Memory/Adapter/Plugins/Config/Console/Logs/Test/Delegation）
- 插件项目：Plugins/Plugin.BasicTools/（speak + send_media）
-           Plugins/Plugin.WorkingTools/（pinboard + thinking_notes + retain_list + mark_for_review）
-           Plugins/Plugin.MemoryTools/（memory，依赖 IMemoryAccess）
-           Plugins/Plugin.FileTools/（read_text + write_text + list_dir + move/delete/copy）
-           Plugins/Plugin.FileOps/（archive_create/extract/list + search_files + grep_files + file_info + file_hash + compare_files）
-           Plugins/Plugin.GroupFileTools/（分组文件操作）
-           Plugins/Plugin.CrossLoopTools/（send_request + evaluate_request + complete_request + send_notify + cancel_request + report_progress + check_messages + respond_to_request + list_requests + list_loops）
-           Plugins/Plugin.SystemTools/（create_sub_agent + stop_sub_agent）
-           Plugins/Plugin.ReviewTools/（review_* 15个复盘工具）
-           Plugins/Plugin.ScheduledTasks/（定时任务，插件化实现）
-           Plugins/Plugin.Email/（邮件收发：send_email, check_unread, check_email, read_email, search_email, download_attachment, delete_email, mark_all_read, list_folders）— Global
-           Plugins/Plugin.SkillTools/（技能工具：casual-chat / code-review / system-maintenance）
-           Plugins/Plugin.NetworkTools/（网络工具）
-           Plugins/Plugin.SshTools/（SSH 工具）
-           Plugins/Plugin.WebSearch/（网页搜索）
-           Plugins/Plugin.DicePool/（骰子系统）
-           Plugins/Plugin.ExternalDice/（外部骰子）
-           Plugins/Plugin.ImageTools/（图片处理工具）
-           Plugins/Plugin.SvgTools/（SVG 渲染）
-           Plugins/Plugin.DocumentTools/（文档处理）
-           Plugins/Plugin.QuickActions/（快捷操作）
-           Plugins/FileToolKit.Shared/ — FileToolBase 抽象基类（路径解析/沙箱/快捷方法），文件类插件共享引用
- 工具配置：Storage/Engine/ToolProfiles.json + Storage/Engine/ComponentConfig.json
- 配置文件：Storage/ 目录下