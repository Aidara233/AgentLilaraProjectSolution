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
- 上下文压缩：超过 80k tokens 触发，保留最近 5 轮 + 摘要
- 关注列表：系统循环下发规则，频道循环语义匹配
- 睡觉系统：SystemEngine 定期评估大睡需求（需管理员许可），DreamEngineSpawnCheck 自动处理小睡/走神
- 睡眠打断分级：走神被@醒、小睡需@+关键词叫醒（仅@触发梦话）、大睡仅管理员/任务可唤醒
- 睡眠期消息拦截：ChannelEngineSpawnCheck 按 CurrentSleepState 拦截，消息入库但不响应，醒来后自动补提取记忆
- Prompt Caching：Claude 系 Core 启用 promptCaching，中转站已验证兼容
- Token 统计：ModelCallLog 数据库表记录每次调用，WebUI /logs/tokens 按 Core/模型聚合 + 缓存命中率
- 模型日志结构化：JSON 格式（含 usage + caller tag），WebUI /logs/model 展示 token 摘要 + 按来源筛选
  - CallerTag 标识调用来源：Channel:{id} / System / SubAgent:{sessionId} / Review:{mode}
- 工具禁用管理：ToolRegistry.DisableTool/EnableTool + ToolConfig.json 持久化 + ToolStatusModule 动态注入 + WebUI /config/tools
- Express 工具（fire-and-forget）：ToolMetaAttribute.ExpressAvailable=true 标记轻量工具，Express 模式下原生 tool_use，结果不回注不续轮。escalate 工具替代 [ESCALATE] 文本标记切换到 Working 模式。非 native 提供商保留文本 fallback。
- 多模态图片处理：ContentPart 支持 text/image(path或base64)、ContextBuilder 图片感知（<img id="N"/>标记+直传/描述分流）、ViewImageTool Working专用、SkiaSharp 缩略图

## 冷启动

如果你刚进入对话或经历了上下文压缩，按以下顺序恢复上下文：

1. 读取 `docs/architecture-map.md` — 概要地图，一页纸恢复基本认知
2. 需要某个模块的细节时再读 `docs/architecture.md` 的对应章节
3. 检查 `~/.claude/projects/.../memory/project_progress.md` 了解当前工作进度

不要一次性读取所有源代码文件。按需读取，用到哪个读哪个。

## 项目语言

- 代码注释、文档、讨论：中文
- commit message：英文
- 类名/方法名/变量名：英文

## 编码约定

- .NET 8, C#, SQLite-net-pcl, Newtonsoft.Json
- 新引擎：实现 ISubEngine，通过 SpawnCheck 注册或 ctx.StartEngine() 直接启动
- 新工具：实现 ITool（AgentLilara.PluginSDK），放在 Plugins/ 下独立项目，引用 SDK 编译为 DLL。参数名必须英文（Bedrock 代理限制）
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
   taskkill /IM AgentCoreProcessor.exe /F 2>/dev/null; dotnet build && dotnet run
   ```
   杀进程不会造成数据损坏（SQLite WAL 模式 + 配置文件原子写入）。
2. `git commit` 提交改动，仓库在 solution 内而非工作目录内
3. `--test` 模式试运行验证（需要模拟对话节奏时加 `--delay N`）
4. 更新文档，方便下次冷启动
  - 文档主要包括`E:\Workspace\AgentLilaraProject\AgentLilaraProjectSolution\docs`下的文档，应当尽可能让所有文档都集中在这里面，方便管理。
  - 可以顺带把旧计划文档清空，方便下次填写。
  - 以及这个文档——`CLAUDE.md`本身，很多内容可能不需要更改，但如有必要（比如用户多次提醒过某件事、出现了新的技术需要明确约定）可以加进来，防止遗忘。

不要等用户提醒，做完改动就走这几步。

## 关键路径

- 入口：Program.cs（--file / --debug / 默认 Web 服务器）
- 引擎内核：Engine/MasterEngine.cs
- 频道循环：Engine/Worker/ChannelEngine.cs
- 系统循环：Engine/System/SystemEngine.cs
- 通信桥梁：Engine/Core/TaskBridge.cs
- 委托注册：Engine/Core/DelegationRegistry.cs
- 子agent会话：Engine/System/TaskSession.cs
- 循环闸门：Engine/Core/Gate.cs（delegate 驱动，组合不继承）
- Agent 循环：Engine/Core/Agent.cs（多轮推理，退避策略）
- Agent 宿主接口：Engine/Core/IAgentHost.cs（BuildStartInjectAsync/BuildRoundInjectAsync）
- Agent 配置：Engine/Core/AgentConfig.cs（轮次/退避/压缩阈值）
- 三层压缩：Engine/Modules/CompressionTierModule.cs（L1提示/L2提醒/L3硬保底）
- 压缩工具：Tool/Core/CompressTool.cs（模型可调用，所有模式可用）
- 注入接口：Engine/Core/IInjectProvider.cs（IInjectProvider + InjectContext，替代 BuildPromptSection）
- 生命周期：Engine/Core/IEngineLifecycle.cs（OnInitializeAsync/OnShutdownAsync）
- 信号类型：Engine/Core/ChannelSignal.cs（NewMessageSignal/BusEventSignal/CompressionSignal/ModeSwitchSignal）
- 模块总线：Engine/Core/ModuleBus.cs（每引擎独立 pub/sub，替代 ILoopBus + ComponentEventBus）
- 频道持久化：Engine/Worker/ChannelContextPersistence.cs（per-channel JSON 原子写入）
- 统一模型调用：Core/AgentCore.cs（Express/Working 分流）
- 记忆检索：Memory/MemoryService.cs
- 做梦调度：Engine/Dream/DreamEngineSpawnCheck.cs
- 做梦执行：Engine/Dream/DreamEngine.cs
- 睡眠状态：Engine/Core/SleepState.cs
- 视觉引擎：Engine/Vision/VisionEngine.cs（图片描述+OCR调度）
- 视觉模型：Client/SiliconFlowVisionProvider.cs + Storage/Core/VisionProvider.json
- OCR模型：Client/SiliconFlowOcrProvider.cs + Storage/Core/OcrProvider.json
- 图片存储：Adapter/ImageStorage.cs
- Token 统计：Database/ModelCallLog.cs + ModelCallLogRepository.cs
- 工具管理：Tool/ToolRegistry.cs（禁用逻辑）+ Storage/ToolConfig.json
- 核心工具：Tool/Core/CoreTools.cs（wait + continue_loop）+ Tool/Core/EscalateTool.cs + Tool/Core/ManageComponentsTool.cs
- 插件 SDK：AgentLilara.PluginSDK/（共享契约，插件引用此项目）
- 插件加载：Tool/Host/PluginLoader.cs（扫描 {BaseDirectory}/Plugins/）
- 记忆桥接：Tool/Host/MemoryAccessImpl.cs（IMemoryAccess → Repository + Embedding）
- 插件项目：Plugins/Plugin.BasicTools/（speak + send_media）
-           Plugins/Plugin.WorkingTools/（pinboard + thinking_notes + retain_list）
-           Plugins/Plugin.MemoryTools/（memory，依赖 IMemoryAccess）
-           Plugins/Plugin.FileTools/（read_text + write_text + list_dir + move/delete/copy）
- 工具配置：Tool/Host/ToolProfileManager.cs + Storage/Engine/ToolProfiles.json
- 配置文件：Storage/ 目录下
