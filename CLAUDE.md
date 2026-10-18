# Agent Lilara — AI 协作指引

## 项目状态

**核心特性**：
- 会话体系：频道响应全面会话化（express/working/task 都是 RuntimeSession）+ 基础设施引擎层（ISubEngine）+ 资源池树（SessionPool，频道占用在池里）
- 基础设施引擎：SessionEngine（会话生命周期+资源池+30s循环+express 空闲回收+提取空闲补一次）、MessageEngine（双向消息枢纽，入向去重+分发、出向入库+路由）、AdapterEngine（适配器管理+频道融合+消息分段）、MindEngine（心智引擎：唯一写入口 Writer+情绪+可信度仲裁+检索+伴随提取，常驻）、VisionEngine（图片描述+OCR）、TimerEngine（定时触发）
- 统一会话：RuntimeSession 不分子类，行为由 CreateSessionSpec（组件列表+端口配置+初始状态）+ Type（express/channel/task）驱动。express（轻量对话，权重触发）、Working（频道全功能，由 escalate 创建）、Task（任务执行，禁止套娃）
- 无强制 stop：Session 不设 MaxLifetime/IdleTimeout 上限，仅由模型调 end_session / 外部 RequestStop / CTS 取消终止；express 会话空闲回收（ColdTimeoutSeconds）直接杀
- express 会话化：无独立 ExpressEngine 引擎，无冲动值。无占用频道收到消息 → ExpressEngineSpawnCheck 孵化 Type="express" 会话（AutoClaim Full），按端口权重激活。escalate 同池换 working（claim 原地不动）
- 端口模型：Session 通过 IInputPort（注入上下文）和 IOutputPort（工具写端口）与外界交互，端口决定工具注册
- 消息流：AdapterEngine → MessageEngine（入向去重+分发）→ 订阅者（express/working 会话）；入向咽喉 ExpressEngineSpawnCheck 同时喂 MindEngine 伴随提取攒批；出向 Session → MessageEngine → AdapterEngine
- 引擎容错：API 调用失败不杀循环，连续失败 exponential backoff，SpawnCheck 检测死亡自动重启
- 优雅退出：ApplicationStopping 钩子 → 停适配器 → 停引擎（signal gate + CTS）→ 等 30s → 强杀兜底 → close startupSignal
- 引擎生命周期信号：每个引擎 RunAsync 用 Signal.Continue 创建 lifecycle span（scope=自己），退出时 Close 带 reason
- 睡觉系统：旧 DreamEngine 已随记忆重构移除（force-sleep 信号仍发布，暂无消费引擎）；维护/做梦按 docs/memory-redesign/03 规划走 MaintenanceEngine + Dream 探索者，尚未实装
- 睡眠打断分级：走神被@醒、小睡需@+关键词叫醒（仅@触发梦话）、大睡仅 force-wake 信号可唤醒
- Prompt Caching：Claude 系 Core 启用 promptCaching，中转站已验证兼容
- Session 持久化（已实现）：三件套 spec.json（创建一次写）/ state.json（每轮原子重写）/ history.jsonl（每轮追加），重启续跑 + 死的不加载（仅恢复 status ∈ {Running, Crashed} 的存档，Completed 留档）
- 会话多核切换（已实现）：session 与 core 解耦，`Storage/Session/{type}.json` 的 `AllowedCores` 白名单（缺省=仅默认核，零回归）；`RuntimeSession` 运行时换核（模型 `switch_core` 工具 / `!core` 命令 / WebUI 切换表单），只换模型配置执行器、不碰历史/端口/组件；`state.json.activeCoreConfig` 持久化，compact/重启承接
- 上下文压缩（已激活）：compact 工具不是扳机而是接管——模型调 compact(summary) → 旧 session 退出 + 新 session 创建承接摘要。三层阈值 L1=80k / L2=130k / L3=150k（基于 200k 上下文模型），L3 锁定工具表只剩 compact
- Token 统计：ModelCallLog 数据库表记录每次调用（含 usage + caller tag）。WebUI 已有 LogsProvider（`/p/logs/tokens` 和 `/p/logs/model`）提供可视化。
  - CallerTag 标识调用来源：Session:{sessionId}（express/working）/ Task:{sessionId}（task），在 RuntimeSession.GetOrCreateCore 注入 core；模型调用 span detail 的 `caller` 字段同步携带（TokenAggregator 键名为 caller/cached_tokens，勿改回）
- 工具管理：ToolRegistry 动态注册/卸载 + WebUI /p/plugins 管理
- 消息段交错：MessageSegment（Text/Image/At/Reply）按原文顺序排列。OutgoingMessage.Segments 优先于旧 Content+Attachments 路径。OneBotActions 直接消费 Segments 构建分段消息数组。
- 图片发送：本地图片通过 base64:// 编码传给 NapCat（不依赖 file:/// 或裸路径）。
- 多模态图片处理：ContentPart 支持 text/image(path或base64)、图片感知（img 标记+直传/描述分流）、SkiaSharp 缩略图
- 时区约定（2026-08-23 起）：存储全 UTC + 展示转本地 + agent 调用参数按本地解析。入库咽喉统一 `TimeUtil.NormalizeToUtc`；展示端 `TimeUtil.ToLocalSafe`（`TimeZoneInfo.Local` 动态跟随，勿硬编码 UTC+8）；系统时间注入带动态 offset 锚点。详见 docs/architecture-map.md「时区统一约定」

## 工作规范

- 遇到 claude code 本身异常时，如果出错的是基础工具，立即向用户报告，不要头铁硬试
- fetch 工具通常不可用，获取资料时可能只能通过 web search 的摘要拼凑信息。如果确实需要下载文件，可以要求用户协助
- 如果项目不太顺利，可以停下来找用户协商解决方法，除非你觉得你的方法很合理，否则另辟蹊径之前必须获得用户支持

## 冷启动

如果你刚进入对话或经历了上下文压缩，按以下顺序恢复上下文：

1. 读取 `docs/architecture-map.md` — 架构全景图，一页纸恢复基本认知
2. 检查 memory 索引 `MEMORY.md` 了解记忆文件全景，读 `project_progress.md` 了解当前进度
3. 需要了解特定设计决策时，按需读取对应的 memory 文件（见下方"记忆文件索引"）
4. Storage 目录**不在**解决方案文件夹下，也不在 git 仓库中。运行时实际路径由 exe 目录的 `paths.json` 决定（当前为 `E:\Tools\Lilara\Storage`），与工作目录 `E:\Workspace\AgentLilaraProject\Storage`（配置模板源）分离；改 `Storage/SessionTypes/` 等配置时须同步两处 + `AgentCoreProcessor/templates/`（见 memory `project-qq-forward`）。

不要一次性读取所有源代码文件。按需读取，用到哪个读哪个。

## WebUI 约定（2026-10-18 起）

- **页面作用域数据源**：`DataSourceDefinition.Factory` 为每个页面实例创建数据源；只有无状态源才用 `Source` 共享。有选中对象/编辑草稿的数据源（配置、控制台、适配器、会话类型）必须 Factory，避免跨标签页串状态。
- **显式分页模式**：`DataSourceDefinition.Paging` 必须声明 `Client` 或 `Server`。Server 源按 `DataQuery.Page/PageSize` 返回当前页并给 `TotalCount`；Client 源返回完整集合。表格不再靠 `TotalCount` 猜测。
- **行操作目标**：表格行键用 `TableSchema.RowKeyField`（缺省回退 `_row_id`/`id`/`name`），`SubmitAsync` 收到 payload 已含 `_row_id`；`Danger` 行操作默认需要确认。
- **操作反馈**：`DynamicPage` 统一展示 `ActionResult.Message`，失败不刷新数据、不覆盖草稿；成功后刷新本卡与 `CardDefinition.AffectedDataSourceIds`（精确失效，不要刷新全页）。
- **独立 Trace 页**：正式入口只是 `/logs/trace`（独立 Razor + `log-trace.js`）；`/p/logs/trace` 是兼容跳转壳，`LogTraceProvider` 只提供导航。不要把 Trace 迁入 DynamicPage。
- **验证方式**：UI 回归不能只靠单元测试。用隔离 Storage + 独立 WebUI/HttpBridge 端口启动 `--dev`（不要中断正式部署实例），系统 Chrome + Microsoft.Playwright 的 `ExecutablePath` 驱动；隐藏的 `/p/test/ui-contract` 覆盖分页、行键、确认、失败反馈、配置类型保持，`/p/test/status`、`/p/test/cards` 为卡片演示。


## 记忆文件索引

Memory 目录：`~/.claude/projects/E--Workspace-AgentLilaraProject/memory/`，入口索引为 `MEMORY.md`。历史设计决策记忆已随架构演进清理，当前仅保留近三轮重构的结论：

- `project-session-architecture-review.md` — 2026-08-04 会话架构重构：express 会话化 + 资源池树 + LoopId 移除
- `project-crossloop-removal.md` — 2026-08-03 跨循环消息通讯移除（session 间通讯走子 agent 邮箱）
- `project-dice-removal.md` — 2026-08-03 骰子系统整体移除
- `project-plugin-permission-cleanup.md` — 2026-08-03 插件不再参与权限/引擎配置

更早的设计决策已沉淀到 `docs/`（见下方索引）或随代码演进被废弃；如需追溯历史方案可查 git log。

## 文档索引

项目文档集中在 `docs/`，以下是完整索引：

**架构：**
- `docs/architecture-map.md` — **架构全景图**（引擎生态/消息流/做梦/记忆/工具/WebUI/日志/MCP），冷启动第一站
- `docs/http-bridge-api.md` — **HttpBridge 适配器 API 文档**（HTTP+WS 混合协议：send/messages/channels/status/ws 端点、配置、示例）
- `docs/icalingua-adapter.md` — **Icalingua 适配器对接**（第二条 QQ 通道：Socket.IO 连接/握手、入站/出站映射、配置；协议精要见 docs/ICALINGUA_BRIDGE_SOCKETIO_PROTOCOL.md）
- `docs/review-engine-redesign.md` — ReviewEngine 自由探索重设计**旧稿**（旧复盘系统已随记忆重构移除，仅供参考）


**记忆重构计划（`docs/memory-redesign/`）：**
- `docs/memory-redesign/README.md` — 计划总纲（命名/核心决策/架构总览/文件导航）
- `docs/memory-redesign/01-storage-model.md` — 存储模型：6 表 + 1 JSON、矛盾表达、记录层级、检索
- `docs/memory-redesign/02-emotion-model.md` — 情绪模型：五轴、运动公式、框架调控、SDK 边界
- `docs/memory-redesign/03-memory-engine.md` — **心智引擎整体规划**：Writer 协议、产生端、复活善后
- `docs/memory-redesign/04-migration-todos.md` — 旧系统对照、废弃清单、待定项、实施路线
- `docs/memory-redesign/05-credibility-conflict-model.md` — **可信度与冲突模型**：Credibility/strength、支持/反对边、noisy-OR、冲突组、Review
- `docs/memory-redesign/06-pending-todos.md` — **待定与暂缓清单**：待定（需决策未定）+ 暂缓（明确首版不做）
- `docs/断言存储系统设计决定.md` — 断言侧唯一权威（Claims 遵循此文档）

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
- `docs/core/memory-cores.md` — ExtractionCore（伴随提取，含输出契约与可信度自评规范；旧 7 个记忆核已删）
- `docs/core/context-management.md` — 三层压缩 + CompactTool 接管路径
- `docs/core/special-purpose.md` — 专用核心（SleepTalkCore / PreprocessingCore 已删除，留档说明）
- `docs/core/stream-handlers.md` — NativeToolCallHandler（原生 tool_use 流事件处理）
- `docs/core/configuration.md` — JSON 配置文件格式、协议选择、字段说明

**历史与杂项：**
- `docs/architecture-redesign-spec.md` — 6 月通用会话框架重设计**历史稿**（已被 architecture-map.md 取代，仅供参考）
- `docs/session-persistence-design.md` — 会话持久化实施计划（**已实施**，实现细节以 `Engine/Session/SessionPersistence.cs` 为准）
- `docs/dead-code-cleanup-tracker.md` — 截至 2026-05-24 的死代码清理记录（**历史快照**）
- `docs/audit/` — 2026-05/06 模块审查快照（**历史归档**，不代表当前代码）
- `docs/superpowers/` — subagent 计划/规格工作区（草稿 + 近期计划，非正式文档）

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
3. 运行验证（自包含，不连真实 QQ）：
   - **`--dev` 常驻调试（主）**：`dotnet run -- --dev` → HttpBridge(9876) + WebUI(5000) 常驻，不连 QQ。用 `LilaraConsoleClient --channel <频道>` 或 `curl POST /api/send`（`wait=true` 阻塞拿回复）驱动，可查 WebUI/logs。注意：--dev 无真实频道占用，投递孵 express 会话（无 get_forward_msg），验证 qq-tools 等需先 escalate 升 channel。
   - **`--test` 一次性脚本验证（辅）**：读 FileAdapter input 处理即退，模拟节奏加 `--delay N`；`--test` 只孵 express。
   - 连真实 QQ 的端到端（合并转发统计行/图片注入等）由用户实测。
4. 更新文档，方便下次冷启动
  - 文档主要包括`E:\Workspace\AgentLilaraProject\AgentLilaraProjectSolution\docs`下的文档，应当尽可能让所有文档都集中在这里面，方便管理。
  - 可以顺带把旧计划文档清空，方便下次填写。
  - 以及这个文档——`CLAUDE.md`本身，很多内容可能不需要更改，但如有必要（比如用户多次提醒过某件事、出现了新的技术需要明确约定）可以加进来，防止遗忘。

不要等用户提醒，做完改动就走这几步。

## 关键路径

- 入口：Program.cs（--file / --debug / --test / --dev / 默认 Web 服务器）
- 数据库：Database/DbManager.cs（初始化 + 迁移）+ 18 张业务表 + logs.db
- 会话管理：Engine/Worker/SessionManager.cs（用户映射 + 频道映射 + 消息入库）
- 引擎内核：Engine/Core/MasterEngine.cs（SpawnCheck 注册表 + 活跃引擎表 + 全局组件宿主）
- 引擎孵化：Engine/Worker/ExpressEngineSpawnCheck.cs（入向路由：无占用频道 → 孵化 express 会话）
- 频道响应：Type="express" RuntimeSession（AutoClaim Full，权重触发，空闲回收；无独立 ExpressEngine 引擎）
- 会话引擎：Engine/Session/SessionEngine.cs（30s监控循环，ScheduledTask/TODO扫描，资源池树 + express 空闲回收）
- 资源池：Engine/Session/SessionPool.cs（频道占用在池里：顶层池持有 claim，子 agent 子池；escalate/compact 同池换活跃 session，claim 原地不动）
- 统一会话：Engine/Session/RuntimeSession.cs（IRuntimeSession 实现，行为由 CreateSessionSpec + Type 驱动）
- 消息枢纽：Engine/Messaging/MessageEngine.cs（双向枢纽，入向去重+分发，出向入库+路由）
- 适配器引擎：Engine/AdapterEng/AdapterEngine.cs（适配器生命周期+频道融合+消息分段）
- 提取核心：Core/ExtractionCore.cs（实现 IExtractionLlm，单轮无工具，指令在 ExtractionPrompt.txt）+ Engine/Mind/ExtractService.cs（组合 ExpressExtractor）+ Engine/Mind/ExpressExtractor.cs（攒批 N=20 + 空闲补一次，触发 ExtractionCore 单轮调用）
- 会话规格：AgentLilara.PluginSDK/CreateSessionSpec.cs（组件列表+端口配置+初始状态）
- 会话类型档案：Engine/Session/SessionTypeProfiles.cs（**唯一权限源**，读 Storage/SessionTypes/{type}.json：Core/AllowedCores/Components/Tools 白黑名单/Prompts 自包含；express/channel/task/review 内置种子）
- 会话多核：Engine/Session/RuntimeSession.cs（ConfigureCores/SwitchCore/SetActiveCore + 懒建缓存）+ Tool/Core/SwitchCoreTool.cs（模型 switch_core）+ Command/CoreCommand.cs（!core）+ state.json.activeCoreConfig
- 循环闸门：Engine/Core/Gate.cs（delegate 驱动，组合不继承）+ Engine/Worker/LoopGate.cs（auto-reset 信号）
- Agent 循环：Engine/Core/Agent.cs（多轮推理，退避策略）
- Agent 宿主接口：Engine/Core/IAgentHost.cs（BuildStartInjectAsync/BuildRoundInjectAsync）
- Agent 配置：Engine/Core/AgentConfig.cs（轮次/退避/压缩阈值）
- 组件系统：Component/ComponentHost.cs（实例管理 + ModuleBus 订阅 + 工具注册）
- 组件注册：Component/ComponentRegistry.cs（类型注册，PluginLoader 扫描）
- 全局组件：AgentLilara.PluginSDK/IGlobalComponent.cs / ILoopComponent.cs（接口在 SDK）
- 三层压缩：Engine/Modules/CompressionTierModule.cs（L1提示80k/L2催促130k/L3锁定150k，仅 Evaluate+GetInjectText）
- 压缩工具：Tool/Core/CompactTool.cs（模型调 compact(summary)，新 session 接管旧 session）
- 注入接口：Engine/Core/IInjectProvider.cs（BuildStartInjectAsync/BuildRoundInjectAsync）
- 生命周期：Engine/Core/IEngineLifecycle.cs（OnInitializeAsync/OnShutdownAsync）
- 信号类型：Engine/Core/ChannelSignal.cs（NewMessageSignal/BusEventSignal/CompressionSignal/ModeSwitchSignal）
- 模块总线：Engine/Core/ModuleBus.cs（每引擎独立 pub/sub）
- 频道持久化：Engine/Worker/ChannelContextPersistence.cs（per-channel JSON 原子写入）
- 上下文构建：Engine/Context/ChannelContextService.cs（express/working 会话共用的上下文格式化）
- 上下文状态：Engine/Context/ChannelContextState.cs（频道完整上下文快照：参与者+历史+系统信息）
- 会话宿主：Engine/Session/SessionAgentHost.cs（RuntimeSession 端口驱动的 Agent 宿主实现）
- 统一模型调用：Core/AgentCore.cs（按配置名加载 ExpressCore/ChannelCore/TaskCore + 原生 tool_use；SessionCore 原名 WorkingCore 已随死核清理删除）
- 记忆检索：Engine/Mind/RetrieveService.cs（关键词 + 时间窗口 + 命中关键词数优先 + c_actual 降序 + 二跳内子证据）
- 记忆写入口：Engine/Mind/Writer.cs（唯一写入口：WriteBatch 原子批次，关键词关联 + 同源分组 + 冲突检测 + 证据重算 + 情绪应用）
- Embedding：Client/SiliconFlowEmbeddingProvider.cs（bge-large-zh-v1.5，SiliconFlow）+ Client/OnnxEmbeddingProvider.cs（本地模型）
- 记忆提取入口：ExpressEngineSpawnCheck.HandleMessageAsync（入向咽喉喂攒批，fire-and-forget）+ SessionEngine 30s 循环（空闲补一次）+ Program.cs --test 收尾（FlushAllAsync 清尾巴）
- 复活善后：Engine/Mind/Resurrector.cs（订阅 session 完成 → 从存档重建 → 换记忆工具组 → 注入整理任务）
- 情绪服务：Engine/Mind/EmotionService.cs（唯一情绪写入者：五轴 + 实时应用 + EmotionState.json 原子写 + 快照）
- 事件接收：Engine/Mind/EventSink.cs（框架事件 → L0/L1 落库 FrameworkEvents）
- 证据聚合：Engine/Mind/EvidenceEngine.cs（noisy-OR + logit/sigmoid 算 c_actual）+ Engine/Mind/EvidenceMath.cs
- 排他冲突触发：Engine/Mind/Writer.cs（attribute 谓词首建标记 IsExclusive=true，默认排他；解除后 sticky 不再升回）+ Engine/Mind/ConflictService.cs（同主语+排他属性不同值 → 建组/入组）
- 待验证队列：Engine/Mind/ReviewQueueService.cs（ReviewQueue 统一工作源：入队/认领/状态/崩溃恢复）
- 暂存提交：Engine/Mind/ReviewStage.cs（git 式暂存区，三类操作：可信度调值 / 解除排他 / 拆解冲突组；review_preview 预览 + review_commit 提交）
- Review 消费：Engine/Session/SessionEngine.cs 30s 循环按需孵化 review 会话（队列有活 + 无活跃 review），per-session 注册 IReviewStageAccess（CreateSessionSpec.SessionServices）
- 心智引擎：Engine/Mind/MindEngine.cs（持有 Writer/Emotion/Evidence/Conflicts/Queue/Retrieve/Events/Resurrector）+ Engine/Mind/MindEngineSpawnCheck.cs（响应系统启动，常驻 AutoStart）
- 梦话生成：已随做梦系统删除（旧 DreamEngine 链路移除，无 SleepTalkCore）
- 睡眠状态：Engine/Core/SleepState.cs
- express 空闲回收：Storage/Engine/ImpulseConfig.json（仅 ColdTimeoutSeconds 阈值；冲动值已删，Express 不再有冲动决策）
- 视觉引擎：Engine/Vision/VisionEngine.cs（图片描述+OCR调度）
- 视觉模型：Client/SiliconFlowVisionProvider.cs + Storage/Core/VisionProvider.json
- OCR模型：Client/SiliconFlowOcrProvider.cs + Storage/Core/OcrProvider.json
- 图片存储：Adapter/ImageStorage.cs
- Token 统计：Database/ModelCallLog.cs + ModelCallLogRepository.cs
- 信号系统：Logging/Signal.cs（静态门面）+ Logging/LogDatabase.cs（独立 SQLite）+ Logging/LogWriter.cs（批量写入）+ Logging/SignalConfig.cs（MinLevel 运行时开关，Storage/Logs/signalconfig.json；Debug=0 排查用）
- 信号查询：Logging/LogQuery.cs + Logging/LogAccessImpl.cs（ILogAccess 桥接）
- 每工具 span：ToolExecutor.RunSingleAsync 为每个工具开 LogGroup.Tool span（输入→status/data/error），会话/子 agent/bootstrap 全覆盖
- 工具管理：Tool/ToolRegistry.cs（注册/卸载/禁用）+ Engine/Session/SessionTypeProfiles.cs（**会话类型文件即唯一权限源**：Storage/SessionTypes/{type}.json 的 Tools 白黑名单裁决工具可见性 + Components 清单裁决组件装配，取代旧 PermissionService/ComponentConfig）
- 核心工具：Tool/Core/CoreTools.cs（wait + continue_loop）+ Tool/Core/EscalateTool.cs
- 插件 SDK：AgentLilara.PluginSDK/（共享契约：ITool/IInjectProvider/服务接口/CardSchema）
- 插件加载：Tool/Host/PluginLoader.cs（AssemblyLoadContext 隔离，多类型发现 + 构造注入）
- 心智桥接：Tool/Host/WriterAccessImpl.cs + RetrieveAccessImpl.cs + EmotionAccessImpl.cs（SDK 服务桥：IWriterAccess / IRetrieveAccess / IEmotionAccess）
- 适配器接口：Adapter/IAdapter.cs + AdapterManager.cs + AdapterFactory.cs
- OneBot 适配器：Adapter/OneBot/OneBotAdapter.cs（WS连接 + 消息映射 + QQ协议）+ Adapter/OneBot/ForwardMessageTree.cs（合并转发递归树：嵌套统计+统计行+全文渲染，深度 ForwardMaxDepth 可配，树缓存30）
- File 适配器：Adapter/File/FileAdapter.cs（文件轮询，测试用；SendMessageAsync 返回输出文件名作为消息 id，speak 等工具据此判成功）
- Icalingua 适配器：Adapter/Icalingua/（IcalinguaAdapter.cs 连接+握手+SocketIO 生命周期 / IcalinguaMessageParser.cs 入站 content 伪标记解析+图片下载 / IcalinguaActions.cs 出站 SendMessageParams / IcalinguaConfig.cs / IcalinguaProtocol.cs 常量+ed25519 签名）— 第二条 QQ 通道（Socket.IO + ed25519），与 OneBot 并行按 whitelist 分流；协议精要见 docs/ICALINGUA_BRIDGE_SOCKETIO_PROTOCOL.md
- HTTP 桥接适配器：Adapter/Http/HttpBridgeAdapter.cs（HTTP+WS 混合，独立端口自建 Kestrel；POST /api/send 阻塞等突发统一返回 + /api/messages 聊天记录 + /ws 双向实时；实现 IEngineAwareAdapter/IChannelAwareAdapter 拿引擎 + 频道归属；多适配器出向路由按 IChannelAwareAdapter.OwnsChannel 优先）— `--dev` 常驻调试模式经它驱动（LilaraConsoleClient/curl）
- 迷你客户端：LilaraConsoleClient/（net8.0 控制台，零依赖，直连 HttpBridge ws 端点交互式对话；协议见 docs/http-bridge-api.md §6）
- 适配器引擎访问：Adapter/IEngineAwareAdapter.cs（MasterEngine 注入）+ Adapter/IChannelAwareAdapter.cs（频道归属），AdapterManager.AttachEngine 由 MasterEngine 构造注入
- MCP 系统：MCP/McpServerManager.cs + McpBridgeTool.cs + McpConfig.cs
- WebUI Shell：WebUI/Shell/ProviderRegistry.cs + WebUI/Components/Shell/（DynamicPage/CardGrid/CardHost.razor）
- WebUI 数据层：WebUI/Shell/DataSourceManager.cs（Fetch重试 + Subscribe推送 + 路由参数注入）
- WebUI 卡片类型：WebUI/Components/Cards/（Table/Status/Form/Stream/Chat/Action/Tree/Detail/PropertyEditor 共 9 张 + Custom 预留）
- WebUI Provider：WebUI/Providers/（Dashboard/Engines/EngineDetail/Sessions/Channel/Vision/Dream/Review/Memory/Adapter/Config/Console/Test/LogTrace/Plugins/Logs/Currency，17 个；Dream=框架事件+推断断言，Review=ReviewQueue+冲突组，Memory=Claims/Journals/Keywords/情绪快照）
- 插件项目：Plugins/Plugin.BasicTools/（speak + thinking + send_media + send_file + adapter_action + list_channels + search_chat_history + list_sessions + send_session_message）
-           Plugins/Plugin.WorkingTools/（thinking_notes + retain_list + mark_for_review + task_management；Global 组件 pinboard：文件式系统记忆）
-           Plugins/Plugin.MindTools/（journal/claim/remember/mind_status/archive + mind-emotion/mind-experience 注入组件）
-           Plugins/Plugin.FileTools/（read_text + write_text + update_text + list_dir + move_file/delete_file/copy_file）
-           Plugins/Plugin.FileOps/（archive_create/extract/list + search_files + grep_files + file_info + file_hash + compare_files）
-           Plugins/Plugin.GroupFileTools/（分组文件操作）
-           Plugins/Plugin.SystemTools/（子 session 管理：create_sub_agent / stop_sub_agent / send_instruction / list_sub_agents / wait_sub / get_sub_info；子侧回报：report_to_parent）— system-ops（父侧 LoopComponent）+ sub-agent-runtime（子侧 LoopComponent）
-           Plugins/Plugin.ReviewTools/（review_list/resolve/preview/commit/evidence/declassify/dissolve/finish 验证工具链，review-tools 组件）
-           Plugins/Plugin.ScheduledTasks/（定时任务，插件化实现）
-           Plugins/Plugin.Email/（邮件收发：send_email, check_unread, check_email, read_email, search_email, download_attachment, delete_email, mark_all_read, list_folders）— Global
-           Plugins/Plugin.SkillTools/（技能工具：casual-chat / code-review / system-maintenance）
-           Plugins/Plugin.NetworkTools/（http_request + download_file + Playwright 浏览器自动化 10 个工具：navigate/click/fill/extract/screenshot/js/cookies/wait/close）— Global + Loop
-           Plugins/Plugin.SshTools/（SSH 工具）
-           Plugins/Plugin.WebSearch/（网页搜索）
-           Plugins/Plugin.GitTools/（git_* + github_* 仓库操作）— Loop + Global
-           Plugins/Plugin.Currency/（货币工具）— Global
-           Plugins/Plugin.CampusPrint/（校园打印）— Global
-           Plugins/Plugin.ImageTools/（图片处理工具）
-           Plugins/Plugin.SvgTools/（SVG 渲染）
-           Plugins/Plugin.DocumentTools/（文档处理）
-           Plugins/Plugin.QuickActions/（快捷操作）
-           Plugins/Plugin.QQTools/（QQ 合并转发：入站统计行 + get_forward_msg 工具 + 图片下载注入）
-           Plugins/FileToolKit.Shared/ — FileToolBase 抽象基类（路径解析/沙箱/快捷方法），文件类插件共享引用
- 异步唤醒链路（session-scoped）：
  后台任务完成 → GlobalComponent 回调 ISessionResourceAccess.ResolveActiveSession(sessionId) 解出活跃 session → SessionEngine.WakeUpSession → RuntimeSession.WakeUp（_wakeSignaled）→ WaitForActivationAsync 检测 → 突发入口先跑 OnBeforeInvokeAsync + BuildPromptSections → 注入 _history → 进入 Agent 推理
  - session-scoped 子资源：子 agent / 下载 / SSH 实装 ISessionResource，注册到 SessionEngine；compact 换根由资源池统一处理（任务键裸 sessionId，无手动 rebind），session 死时通知清理
  - end_session 拦截：子 agent + 任意 ISessionResource HasActiveResources=true 都阻止退出；二次 end_session force override
- 异步唤醒链路（channel-scoped）：
  定时任务到期 → ScheduledTasksTimerComponent 按 requires_work 路由 → MasterEngine.WakeLoop(sessionId) → SessionEngine.WakeUpSession（裸 sessionId，经池解析跨 compact forward）→ wake 活跃会话
- 工具配置：Storage/ToolConfig.json（全局工具禁用）+ 组件初始化超时收敛为代码常量 Component/ComponentTimeouts.cs（GlobalInitTimeoutSeconds/LoopInitTimeoutSeconds/ShutdownTimeoutMs，防挂起阻塞启动/会话）
- 配置文件：Storage/ 目录下

## 发布流程（重新 publish 主程序）

1. 备份旧部署：`cp -r E:/Tools/Lilara/publish E:/Tools/Lilara/publish.bak-<日期>`
2. `dotnet build -c Release`（全解决方案）——**必须整解决方案构建**，让所有插件（含新增 QQTools）编译并复制到 `bin/Release/net8.0/win-x64/Plugins/`（单独 build 主工程不会构建插件）
3. **publish 前清空部署目录**（只保留 `paths.json`）：publish 到已存在目录不会清理旧文件，旧版 `Microsoft.Extensions.Logging` 等残留 DLL 会导致启动 `FileNotFoundException`。`paths.json` 不在发布产物中，务必先取出再放回。
4. `dotnet publish AgentCoreProcessor/AgentCoreProcessor.csproj -c Release -o E:/Tools/Lilara/publish`（主程序 + WebUI + templates）
   - csproj 已开 `PublishSingleFile`：托管程序集全部打进 `AgentCoreProcessor.exe`（约 110MB），根目录只剩 exe + 原生库（`e_sqlite3`/`libSkiaSharp`/`onnxruntime*`）+ `aspnetcorev2_inprocess.dll`（IIS 宿主，删不得）+ 数据目录。
   - `IncludeNativeLibrariesForSelfExtract` 保持 false（不启用）；`PublishTrimmed` 必须保持 false —— 插件反射扫描 / Newtonsoft / Blazor 一裁剪即启动失败。
5. 复制插件：`cp -r AgentCoreProcessor/bin/Release/net8.0/win-x64/Plugins E:/Tools/Lilara/publish/Plugins`（**注意 cp 到已存在目录会嵌套成 Plugins/Plugins，先确保目标不存在**）
6. 验证：`E:/Tools/Lilara/publish/AgentCoreProcessor.exe --dev` 起动（HttpBridge 9876 + WebUI 5000，不连 QQ）+ 确认 23 个插件从 `publish/Plugins/` 加载 + 会话 components 含目标组件
7. 连真实 QQ 的端到端由用户实测（OneBot WS 连不连得上以内网 `debian.local:3003` 为准）

注意：`templates/Session` 等旧结构残留无实际影响（SessionTypes 才是唯一权限源），可忽略不清理。
## 历史数据内存优化（2026-08-19）

- 根因：Server GC（Web SDK 默认）16 核预提交 6-7GB + GC 的 LOH 段只回收对象不还 OS（启动时大量历史对象 inflate 后段保留）。
- 处置：csproj 显式关 Server GC；清理测试污染（9df5c67dfb82 会话 819MB history.jsonl —— 30 行重复字符测试数据）。
- 优化代码：Agent 发送给模型的历史限量（MaxMessagesToSend=100 条 + 前缀归档摘要）；SessionPersistence.LoadHistory 只读尾部 1000 行；AppendHistoryAsync 裁剪 >64KB 的 base64/tool 内联负载。
- 排查工具链：VMMap（/tmp/vmmap/）+ dotnet-gcdump/dotnet-dump（~/.dotnet/tools/）。诊断经验：内存大头看 VMMap 的 Managed Heap committed vs 活对象，区分段持有 vs 真占用。
