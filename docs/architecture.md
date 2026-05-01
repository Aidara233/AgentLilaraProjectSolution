# Agent Lilara 架构文档

## 项目概览

Agent Lilara 是一个多平台 AI Agent 框架，接收来自不同平台的用户消息，经过分类、记忆检索、工具调用后，以人格化的方式回复用户。核心特色是"赛博睡眠"机制——空闲时自动进行离线记忆整理和全局复盘，以及"双循环架构"——频道循环处理用户交互，系统循环负责任务调度和跨频道协调。

**技术栈**：.NET 8 / C# / SQLite-net-pcl / Newtonsoft.Json / Anthropic SDK / OpenAI SDK / SiliconFlow API / bge-large-zh-v1.5 embedding

**目录结构**：

```
AgentCoreProcessor/
├── Adapter/          平台适配层（Console/File/OneBot QQ）
├── Client/           IModelClient 抽象层（Claude/OpenAI 双协议）+ Embedding
├── Command/          框架指令系统
├── Config/           路径配置
├── Core/             业务核心（11个 Core 类）
├── Database/         数据实体 + Repository
├── Engine/           引擎生态（调度中心）
├── Memory/           记忆服务
├── Tool/             工具系统
├── Util/             工具类
└── Program.cs        入口（--qq / --file / --test / --mute / 默认 Console）
```

---

## 适配器层 (Adapter/)

纯粹的消息收发适配层，不含业务逻辑。

### 接口与管理

- **IAdapter** — 统一接口：`StartAsync()` / `SendMessageAsync(OutgoingMessage)` / `OnMessageReceived` 事件
- **AdapterManager** — 管理所有 Adapter 实例，提供按平台名发送的统一入口 `SendMessageAsync(platform, message)`

### 实现

- **ConsoleAdapter** — 控制台交互，开发调试用
- **FileAdapter** — 文件夹模式：`/input/` 下 .json(带元数据) 或 .txt 每文件一条消息，`/output/` 下每条回复一个文件。支持模拟群聊、不同用户、@提及、时间戳等。`--file` 参数启用，`--test` 一次性读取后自动退出
- **OneBotAdapter** — 通用 OneBot v11 适配器，正向 WebSocket 连接 NapCat。消息段解析(text/at/reply/image)、文本提及检测(botNames配置)、黑白名单频道过滤、指数退避重连。`--qq` 参数启用

### 消息格式

- **IncomingMessage** — Platform / PlatformUserId / ChannelId / Content / IsPrivate / IsMentioned / DisplayName / Nickname / ReplyTo / Time / Attachments
- **OutgoingMessage** — ChannelId / Content / ReplyTo / Attachments

Adapter 接收平台消息 → 转为 IncomingMessage → 通过 EventBus 发布 MessageEvent。

---

## API 客户端 (Client/)

### IModelClient 抽象层

统一接口：`StreamAsync(messages, config) → IAsyncEnumerable<string>`，支持多模态内容（ContentPart: text/image）。

- **ModelClientBase** — 共享历史管理逻辑
- **ClaudeModelClient** — Anthropic SDK（AnthropicClient + StreamClaudeMessageAsync），支持 system 提取、content blocks、thinking block 映射、base64 图片
- **OpenAIModelClient** — OpenAI SDK（ChatClient + CompleteChatStreamingAsync），支持自定义 Endpoint

**ModelClientFactory** — 根据配置 `provider` 字段（"claude"/"openai"）创建对应客户端。

### ApiClientCfg

JSON 配置类，每个 Core 对应一个配置文件 `Storage/Core/{CoreName}.json`：
- apiKey / apiEndpoint / model / provider
- temperature / maxTokens / topP / frequencyPenalty / presencePenalty
- stream（始终 true）
- extraBody（如 `{"thinking": {"type": "enabled"}}`）
- conversationHistory（预设的系统提示词）

### Embedding

- **IEmbeddingProvider** — 接口：`GetEmbeddingAsync(text) → float[]`
- **SiliconFlowEmbeddingProvider** — 云端实现，使用 bge-large-zh-v1.5 模型

---

## 配置 (Config/)

### PathConfig

静态类，管理所有绝对路径。从 `paths.json` 加载：
- `StoragePath` — Storage 根目录
- `DatabasePath` — 数据库目录
- `CoreConfigPath` — Core 配置目录 (`Storage/Core/`)
- `LogPath` — 日志目录

---

## 核心处理层 (Core/)

所有 Core 继承 `CoreBase`，通过类名自动加载 `Storage/Core/{CoreName}.json` 配置。

### CoreBase 抽象基类

提供通用能力：
- **Processor** — Core 与 IModelClient 的桥梁，加载配置并发起请求
- **UsePersona** — 虚属性（默认 true），为 true 时自动注入 Persona.txt 到 system 消息前
- **GenerateAsync()** — 流式生成，支持 `<over>` break 检测，返回 Usage（token 统计）
- **GenerateOnceAsync()** — 一次性生成，支持多模态（文本+图片），返回完整文本
- **ResetProcessor()** — 重置处理器（清除对话历史，重新应用 extraMessage）
- **LogOutput()** — 写入 `Storage/Logs/Model/{timestamp}_{CoreName}.log`

### PreprocessingCore

消息分类路由。Embedding 二分类（聊天/任务）。`UsePersona = false`。

### ExpressCore

人格化聊天输出。`GenerateOnceAsync(input)` 直接聊天回复，支持多模态图片输入。
Prompt 引导分条输出（每行一条独立消息），WorkerEngine 拆分后逐条发送+随机延迟。

### WorkingCore

Agent 循环的中心——通过多轮工具调用完成任务。

**循环结构**（最多 15 轮）：
1. PromptBuilder 组装本轮消息
2. GenerateAsync 调用模型，通过 `<over>` break 解析 ToolCall JSON
3. ToolExecutor DAG 执行
4. 处理特殊工具副作用（完成/思考笔记/说话/记忆/信号）
5. 收集 retain=true 结果
6. 事件驱动间隙：收集新消息
7. 更新滚动状态，进入下一轮

**跨轮状态**：
- `register` — toolId → 输出数据（寄存器，跨轮持久）
- `thinkingNotes` — key → value（思考笔记）
- `retainedResults` — retain=true 的历史结果
- `taskList` — 模型自维护的任务列表

**回调机制**（由 ChannelEngine 在调用前设置）：
- `OnSpeak` — 说话 → Adapter 直发（不经润色）
- `OnMemory` — 记忆 → MemoryService.StoreAsync
- `OnSignal` — 信号 → EventBus.PublishSignal
- `OnReviewHint` — 标记复盘 → ReviewHintRepository.CreateAsync

**子 agent 支持**（已移除）：
- 原有的 SubAgentCore / SubAgentRunner / DelegateTool 已移除
- 子agent 功能由 SystemEngine + TaskSession 替代

### PromptBuilder

每轮动态组装消息列表，注入顺序：
1. 工具描述（ToolRegistry 动态生成，支持工具列表参数）
2. 附加上下文（记忆注入点）
3. 用户原始需求
4. 当前任务列表
5. 思考笔记
6. 历史保留结果（retain=true）
7. 上一轮工具执行结果
8. 新消息 / 子 agent 结果（事件驱动注入）

### MemoryExtractionCore

从对话中提取值得长期记住的事实。输出 JSON 数组（type/content/confidence/sentiment/correction）。
区分 fact（写临时库带 confidence）和 feedback（匹配已有记忆修正）。`UsePersona = false`。

### 做梦片段 Core

- **ConsolidationCore** — 临时记忆整理：模型决定 keep/merge/discard
- **WeightCore** — 记忆权重评估：模型打分 0.0-1.0
- **LinkCore** — 关联重建：模型分析记忆间关系
- **CombineCore** — 记忆组合：模型从强关联记忆中抽象新洞见

### ReviewCore

复盘 LLM 通信。继承 CoreBase，提供 `SetConversation(messages)` 方法供 ReviewEngine 外部设置对话历史。

---
## 数据库层 (Database/)

使用 SQLite-net-pcl，DbManager 提供统一 CRUD + 原始 SQL 查询。`InitAsync()` 自动建表。

### 实体与表

| 实体 | 表名 | 关键字段 |
|------|------|----------|
| Person | Persons | Id, TrustLevel(0-5), TrustProgress |
| User | Users | Id, PersonId→Person, Platform, PlatformId, PermissionLevel, DisplayName, FastMemory |
| Channel | Channels | Id, Name, Affinity(默认1.0) |
| UserMessage | UserMessages | Id, UserId, ChannelId, Content, SenderName, IsFromBot, Time |
| MemoryEntry | Memories | Id, PersonId?, ChannelId?, Content, Embedding, Importance, Confidence, Feedback, IsDerived, SourceHash, LastDreamTime, IsPersistent, ExpiresAt |
| TempMemoryEntry | TempMemories | Id, PersonId?, ChannelId?, Content, Embedding, Confidence, Feedback, SourceMessageId |
| MemoryLink | MemoryLinks | Id, SourceId, TargetId, Strength, LinkType |
| PersonaMemoryEntry | PersonaMemories | Id, Content, Embedding, Category |
| ReviewHint | ReviewHints | Id, Content, PersonId?, ChannelId?, IsProcessed |

注：Topics 表保留但不再使用，TopicId 列保留但不再有意义地赋值。

### 关键 Repository 方法

**MemoryRepository**：GetRecentAsync / GetByPersonAsync / GetByTagsAsync(personId, channelId) / GetUndreamedAsync / GetOldestDreamedAsync / GetBySourceHashAsync / CreateAsync / CreateDerivedAsync / UpdateAsync / SearchAsync

**TempMemoryRepository**：GetAllAsync / GetByTagsAsync(personId, channelId) / CreateAsync / DeleteAsync / ClearAllAsync

**MemoryLinkRepository**：GetLinksForAsync(ids, minStrength) / GetByMemoryIdAsync / CreateOrUpdateAsync

**ReviewHintRepository**：GetUnprocessedAsync / MarkProcessedAsync / CreateAsync / DeleteProcessedAsync

**MessageRepository**：GetRecentByChannelAsync / GetByChannelAsync / SaveAsync

**PersonRepository**：GetAllUserIdsAsync(personId) / MergeAsync(target, source)

---

## 引擎生态 (Engine/)

MasterEngine 是纯"操作系统内核"，不含任何子引擎的业务逻辑。子引擎自治，新增引擎类型不改主引擎代码。

### 双循环架构

**频道循环 (ChannelEngine)**：
- 一个活跃频道一个实例，处理用户交互
- 完全智能（Express/Working 模式），有自主权
- 可以自主处理：记忆读写、只读文件、关注列表匹配后的简单响应
- 需要上报的情况：写入/修改操作、跨频道协调、复杂任务
- 通过 TaskBridge 与系统循环异步通信

**系统循环 (SystemEngine)**：
- 单例，纯调度者定位，不是执行者
- 负责：任务评估、子agent管理、跨频道协调、系统内务
- 不亲自操作文件/命令，只有调度类工具
- 保证响应性：不被长任务阻塞，新任务的评估/拒绝不排队

### TaskBridge 通信机制

频道循环 ↔ 系统循环异步通信桥梁，提供两个队列：

**TaskQueue（重量请求）**：
- DelegateTask — 委派任务给系统循环
- RequestApproval — 请求系统循环审批
- Escalate — 升级到系统循环处理

**NotificationQueue（轻量信号）**：
- Notify — 通知系统循环（不需要响应）
- ProgressUpdate — 进度更新
- WatchHit — 关注规则命中

系统循环 → 频道循环：
- Instruct — 发送指令
- Approve/Reject — 审批结果
- Stop — 停止频道循环
- UpdateWatchRules — 更新关注规则

持久化：TaskBridge 在 Storage/task_queue.json 持久化待处理任务，重启时恢复。

### 三个核心接口

**ISubEngine**（引擎实例）：
- `EngineType` — 类型标识
- `RunAsync()` — 主执行逻辑，结束后 IsAlive 变 false
- `OnEvent(EngineEvent)` — 接收事件
- `IsAlive` — 是否仍在运行
- `RequestStop()` — 请求停止
- `IsInfrastructure` — 默认 false，true 表示基础设施引擎（不影响 IsIdle 判定）

**IEngineSpawnCheck**（引擎类型注册，每种类型一个常驻实例）：
- `OnEventAsync(e, ctx)` — 处理事件，更新内部状态
- `ShouldSpawnAsync(e, ctx)` — 是否需要创建新实例
- `Create(ctx)` — 创建实例

**ISystemContext**（MasterEngine 暴露的"系统调用"）：
- 数据访问：Memories / TempMemories / MemoryLinks / ReviewHints / MemorySvc / Session / Embedding
- 适配器：Adapters
- 事件：EventBus
- 引擎查询：IsIdle / IdleDuration / LastMessageTime / HasActiveEngine / GetActiveEngineCount
- 引擎管理：StartEngine(engine) / RequestStopEngine(engine)
- 通信桥梁：TaskBridge

**IAgentSession**（统一会话接口）：
- ChannelSession — 聊天频道（冲动值/闸门/用户交互）
- TaskSession — 执行类子agent（工具集/任务生命周期）
- MonitorSession — 信息监视（过滤规则/摘要/无用户交互）[预留]

### MasterEngine

实现 ISystemContext。核心职责：

**InitAsync**：初始化 DB → 创建 Repository → 注册 SpawnCheck → 订阅 EventBus → 加载 EngineConfig.json 自启动引擎

**HandleEventAsync**（事件流水线）：
1. 内核更新（MessageEvent → 更新 lastMessageTime）
2. 遍历 SpawnCheck：OnEventAsync → ShouldSpawnAsync → Create → StartEngine
3. 派发 OnEvent 给所有活跃实例
4. 清理 IsAlive=false 的实例

**SpawnCheck 工厂**：
```csharp
["Timer"]   = () => new TimerEngineSpawnCheck(),
["Channel"] = () => new ChannelEngineSpawnCheck(),
["System"]  = () => new SystemEngineSpawnCheck(),
["Dream"]   = () => new DreamEngineSpawnCheck(),
["Command"] = () => new CommandSpawnCheck(),
```

**IsIdle**：排除 IsInfrastructure=true 的引擎后，无活跃引擎即为空闲。

### TimerEngine

心跳引擎。`IsInfrastructure = true`。RunAsync 循环：delay(30s) → EventBus.Publish(TimerEvent("tick"))。通过 EngineConfig.json autoStart 自启动。可响应 "timer-interval" 信号调整间隔。

**Phase 8: SystemEngine 心跳监控**：
- 每个 tick 检查 `ctx.HasActiveEngine("System")`
- 如果 SystemEngine 存在 → 更新 lastSystemHeartbeat
- 如果 SystemEngine 不存在 > 1 小时 → 发送严重故障报警到所有管理员频道
- **不触发睡觉**：SystemEngine 挂了是严重事故，需要人工介入，不应该自动睡觉

### ChannelEngine (频道循环)

长生命周期，一个活跃频道一个实例。统一负责社交决策和消息处理。

**ChannelEngineSpawnCheck**：
- 维护 `activeChannels: Dictionary<int, ChannelEngine>` 按频道路由
- MessageEvent → SessionManager.OnMessageAsync → 权限检查
- 有活跃 ChannelEngine → EnqueueMessage（忙时转发给 WorkingCore 消息通道）
- 无活跃 ChannelEngine → 创建新实例

**消息缓冲聚合**：2.5s 窗口，窗口内新消息重置计时器。

**冲动值决策**：
- 私聊/控制台：缓冲聚合后一定回复
- @提及（CQ at 或文本包含 botNames）：穿透冷却期，必回
- 群聊：impulse ≥ 有效阈值（base 3.0 + ignore boost）
- 累加：BaseMessageScore × channelAffinity × participantFactor（1人=1.0, 2人=0.9, 3人=0.8, 4+=0.6）
- 衰减：每秒 -0.5，发言后冷却 3s
- 负反馈：仅主动发言后才设 awaitingResponse，被忽略时阈值 +1.5（最多 3 次）

**ProcessBatch 流程**：
1. 收集图片附件
2. 构建 XML 上下文（`<participants>` + `<history>` + `<new>`，未回应消息归入 new，@消息标记 `mentioned="true"`）
3. PreprocessingCore Embedding 二分类（聊天/任务）
4. MemoryService.RecallAsync 检索记忆（per-person 缓存，60s TTL）
5. 路由：聊天 → ExpressCore（分条输出+逐条发送+随机延迟）/ 任务 → WorkingCore Agent 循环
6. MemoryExtractionCore 异步提取记忆（每 3 条触发）

**关注列表 (WatchRules)**：
- 系统循环通过 SetWatchRuleTool 下发规则到频道循环
- 规则含自主权级别：NotifyOnly / AutoRespond / Escalate
- 频道循环在 Express prompt 注入规则，模型语义匹配
- 命中后通过 TaskBridge.SendNotification 上报系统循环
- WatchRulesModule 负责规则管理和 prompt 注入

**冷却退出**：600s 无消息 → 退出前强制提取剩余记忆 → IsAlive = false。

ChannelEngineSpawnCheck：MessageEvent → 按 ChannelId 路由。

### SystemEngine (系统循环)

单例，纯调度者定位。每次启动创建新实例。

**SystemEngineSpawnCheck**：
- 单例模式，首次 TickEvent 时创建
- 不响应 MessageEvent（只处理任务和通知）

**闸门驱动循环**：
- LoopGate（auto-reset）：gate.WaitAsync(coldTimeout) → 放行后立即重置 → 收集 → 执行 → 回到等待
- 触发源：TaskBridge 任务/通知 / ContinueLoop自唤醒 / 定时器
- 超时 → 冷却退出

**统一管线**（每轮重建上下文）：
1. WaitGate → 闸门放行
2. CollectTasks → drain TaskBridge 任务队列和通知队列
3. PrepareContext → 重建上下文（活跃子agent/频道列表/任务队列/通知摘要）
4. BuildPrompt → PromptBuilder（注入工具描述+上下文+便签板+思考笔记）
5. CallModel → AgentCore.InvokeAsync（返回工具调用）
6. ProcessResponse → 执行工具+发布事件
7. DecideNext → ContinueLoop(signal) / idle

**上下文持久化**：
- 每轮结束写入 Storage/SystemContext.json（WAL 模式）
- 重启时恢复上下文（便签板/思考笔记/任务队列）
- 包含：conversationHistory / pinboard / thinkingNotes / pendingTasks / subAgents

**上下文压缩**：
- 超过 MaxContextMessages(50) 触发压缩
- 保留最近 10 条 + 压缩摘要（模型生成）
- 压缩后写入持久化文件
- 压缩时保留工具调用和结果的完整性

**子 agent 管理**：
- 通过 CreateSubAgentTool 创建 TaskSession
- 通过 SendToSubAgentTool 发送指令（子agent 被动执行一轮）
- 通过 StopSubAgentTool 停止子agent
- 禁止套娃（子agent 不能创建子agent）
- 子agent 工具集根据创建时指定的白名单动态注册

**工具集**（纯调度+轻量执行）：
- 调度类：CreateSubAgent / SendToSubAgent / StopSubAgent / DeleteSubAgent
- 通信类：SendToChannel / CheckNotifications / SetWatchRule / CheckTaskQueue
- 自用类：便签板 / 思考笔记 / 继续
- 轻量执行：读取文件 / 记忆读写（"看一眼就完事"的操作）

**Phase 8: 睡觉评估**：
- 每 5 分钟定期自检（在读取任务之前）
- 4 因子评分系统：
  - 空闲时长（最高 40 分）：空闲越久越困
  - 未做梦记忆（最高 30 分）：记忆积压越多越困
  - 待复盘标记（最高 20 分）：待处理事项越多越困
  - 距上次睡觉时间（最高 10 分）：距上次大睡越久越困
- 评分 >= 60 触发睡觉请求
- 发送请求到所有管理员频道，等待 `/sleep approve <requestId>` 或 `/sleep deny <requestId>`
- 10 分钟超时自动批准，无管理员自动批准
- 批准后通过 EventBus 发布 `force-sleep` 信号，由 DreamEngineSpawnCheck 执行
- **不干扰上下文**：评估过程不调用模型，不产生对话轮次，不写入 context.jsonl

**任务评估**：
- 系统循环有权拒绝任务（"不是工具人"）
- 判断标准：任务价值、请求者信任等级、是否适合 Lilara 做
- 评估由系统循环自己完成（不单独的评估模块），prompt 引导

### DreamEngine

每次睡觉创建新实例。三级睡眠：

**走神/小睡**：固定片段数循环（走神 1 个，小睡 5 个）。

**大睡**：两阶段制——

Phase 1（浅睡）：集中清临时记忆
- Consolidation 权重极高（20.0f）
- 退出条件：临时记忆清空 / Phase1 预算耗尽（总预算 1/3）/ 时间超限 / shouldWake

Phase 2（深睡）：启动 ReviewEngine + 继续做梦
- 通过 `ctx.StartEngine()` 孵化 ReviewEngine（并行执行）
- DreamEngine 继续跑 Weight/Link/Combine（不再跑 Consolidation）
- DreamEngine 在 ReviewEngine 完成前不退出（陪跑）
- 退出条件：shouldWake 且 Review 完成 / Review 完成且无片段可跑 / 时间超限 / 预算耗尽

**片段权重计算**（ComputeWeights）：
- 走神：Weight(1.0) + Link(1.0)
- Phase 1：Consolidation(20.0 if temps) + Weight(1.0) + Link(3.0 if undreamed) + Combine(0.5)
- Phase 2：Weight(1.0) + Link(3.0 if undreamed) + Combine(0.5)，无 Consolidation

### DreamEngineSpawnCheck

**Phase 8 重构**：简化为信号驱动 + 小睡/走神，大睡决策迁移到 SystemEngine。

**职责**：
- ① 强制睡觉：响应 `force-sleep` 信号（来自 SystemEngine 或 `/sleep` 命令）
- ② 小睡：空闲 > 600s → 直接启动（无需许可）
- ③ 走神：距上次走神 > 120s → 直接启动（无需许可）

**信号处理**（OnEvent）：
- force-sleep → 设置 forceFlag，下次 ShouldSpawn 立即返回 true
- dream-config → 运行时更新配置

**触发条件**（ShouldSpawn）：
1. forceFlag → 立即大睡（DeepSleep 模式）
2. 空闲 > NapIdleThreshold (600s) → 小睡（Nap 模式，5 个片段）
3. 距上次走神 > DaydreamCooldown (120s) → 走神（Daydream 模式，1 个片段）

**移除的功能**（已迁移到 SystemEngine）：
- ❌ 红色/黄色评估逻辑
- ❌ 许可机制（dreamPermission, permissionRequestTime）
- ❌ 统计跟踪（DreamStats, scoreOffset, customRedAlert）
- ❌ 决定锁定机制

**设计理念**：
- 大睡需要主观意愿（管理员许可），由 SystemEngine 决策
- 小睡/走神是自然生理反应（无需许可），由 DreamEngineSpawnCheck 自动处理
- 职责清晰：SystemEngine 负责决策，DreamEngineSpawnCheck 负责执行

**自适应基线**：DreamStats.json 滚动 7 天统计，基线 = 日均临时记忆峰值。连续 3 天红色 → 上调基线 20%。

### ReviewEngine

由 DreamEngine 在大睡 Phase 2 孵化，不注册 SpawnCheck。

**独立 Agent 循环**：
- 9 个专用工具（检索记忆/查看关联/读取消息历史/写入临时记忆/思考笔记/标记复盘/请求增援/保存进度/完成）
- 工具不注册全局 ToolRegistry，由引擎内部管理
- ToolExecutor 通过自定义 toolResolver 使用局部工具集

**四种复盘模式**（加权随机选择）：
- ChannelDaily（频道日报）— 分析频道近期消息和活动
- PersonProfile（人物回顾）— 聚焦某人物的近期互动
- CrossDomain（跨域关联）— 跨频道发现被忽略的联系
- ContradictionDetect（矛盾检测）— 检查记忆库中的矛盾信息

**预注入上下文**：根据选中模式，首轮提示词直接提供高概率需要的信息（频道话题列表、人物记忆、活跃话题摘要等）。未处理的 ReviewHint 也在首轮注入。

**Token 预算制**：
- 基础预算（ReviewTokenBudget）— 每次大睡分配
- 备用预算（ReviewReserveBudget）— 不自动补充，模型通过"请求增援"工具显式申请
- 预算外收尾 — 所有预算耗尽后强制给一轮用于总结/存档
- 每轮提示词注入进度：`累计消耗: X / Y | 备用预算: 可用/已使用`

**产出路径**：发现/结论 → 写入临时记忆 → 下次做梦时 Consolidation 整合入主库

**进度存档**：DreamProgress.json，模型通过"保存进度"工具主动存档，下次大睡可续。

### 配置文件

- **DreamConfig.json** — 做梦调度参数（冷却期、阈值、时间窗口、评分参数、预算配置）
- **DreamStats.json** — 滚动 7 天统计（DailyRecord + DreamBaseline + ConsecutiveRedDays）
- **DreamProgress.json** — 复盘进度存档（ReviewInvestigation 列表）
- **EngineConfig.json** — 自启动引擎列表 `{"autoStart": ["Timer"]}`

### SessionManager

会话管理器，负责用户/频道映射、消息入库。

**OnMessageAsync** 流程：
1. 平台用户 → 内部 User（自动创建 Person）
2. DisplayName 更新（每次消息同步）
3. 频道查找/创建
4. 消息入库（含 SenderName、IsFromBot）
5. 返回 SessionContext（User, Person, Channel, RecentMessages）

**SaveBotMessageAsync(channelId, content)** — bot 回复写入数据库，IsFromBot=true。

### EventBus

发布-订阅事件总线。事件类型：
- **MessageEvent** — 用户消息（Time, Message）
- **TimerEvent** — 定时触发（TimerName）
- **SignalEvent** — 工具信号回环（SignalName, Payload）
- **IdleEvent** / **SystemEvent** — 预留

---

## 记忆系统 (Memory/)

### MemoryService

**StoreAsync** — 写入临时记忆库，自动生成 embedding。

**RecallAsync** — 检索管线：
1. 临时库全量扫描（小库，快）
2. 主库标签 OR 过滤（PersonId / ChannelId，任一匹配即召回，匹配标签数加权）
3. 向量精排（cosine similarity，MinRecallScore=0.25 过滤低相关性）
4. 关联扩展（MemoryLink，可选）
5. 综合排序：Similarity×0.5 + Importance×0.3 + Link×0.2 + TempBoost×0.1
6. PersonaMemory 独立召回（PersonaPenalty=0.05, PersonaMinScore=0.12, 仅聊天路径）
7. 返回 top-K，更新 LastAccessedAt

### 双库架构

- **TempMemory**（临时库）— 缓冲区，写入快，做梦时由 Consolidation 整合入主库
- **Memory**（主库）— 持久存储，带 Importance 权重、关联网络、做梦元数据

### 做梦整合路径

```
用户消息 → MemoryExtractionCore 自动提取 / 工具"记忆"手动写入 → TempMemory
                              ↓ (做梦 Consolidation)
                           Memory (keep/merge/discard)
                              ↓ (做梦 Weight)
                           Importance 调整
                              ↓ (做梦 Link)
                           MemoryLink 关联网络
                              ↓ (做梦 Combine)
                           衍生记忆 (IsDerived=true)
```

---

## 工具系统 (Tool/)

### 接口

- **ITool** — Name / Description / Parameters / Timeout / ExecuteAsync(resolvedInputs, ct) → ToolResult / AllowSubAgent(默认true)
- **ToolParameter** — Name / Description / Index / CanBeRef

### 调用格式

```json
{"tool":"工具名","toolId":"唯一ID","inputs":[{"type":"value","value":"..."},{"type":"ref","source":"其他toolId"}],"output":"输出标识","outputToModel":false,"retain":false}
```

多个工具调用以 `<over>` 分隔。

### ToolExecutor

DAG 执行器，基于 Kahn 拓扑排序 + 分波并行：
- 构建依赖图（从 inputs 的 ref 推导）
- 无依赖的工具并行执行（Task.WhenAll）
- 上游失败 → 下游标记 skipped
- 寄存器跨轮持久化
- 支持自定义 `toolResolver`（ReviewEngine 用局部工具集）

### 工具清单

**全局工具**（ToolRegistry，ChannelEngine 的 Agent 循环用）：

| 工具 | 类型 | 说明 |
|------|------|------|
| 文件流读取器 | 执行型 | 读文件返回内容 |
| 文件流写入器 | 执行型 | 写文件，支持 ref 输入 |
| 说话 | 信号型 | → OnSpeak 回调（直发，不经润色） |
| 完成 | 信号型 | 终止 Agent 循环 |
| 思考笔记 | 信号型 | write/delete key-value |
| 记忆 | 信号型 | → OnMemory 回调 |
| 任务管理 | 信号型 | add/complete/remove 任务列表 |
| 强制睡觉 | 信号型 | `/sleep [daydream\|nap\|deepsleep]` → EventBus "force-sleep" |
| 睡觉许可 | 信号型 | `/sleep approve <requestId>` → EventBus "sleep-approve" (Phase 8) |
| 睡觉拒绝 | 信号型 | `/sleep deny <requestId>` → EventBus "sleep-deny" (Phase 8) |
| 修改睡眠配置 | 信号型 | → EventBus "dream-config" |
| 标记复盘 | 信号型 | → OnReviewHint 回调 |

**系统循环专用工具**（SystemEngine 内部管理）：

| 工具 | 类型 | 说明 |
|------|------|------|
| 创建子agent | 调度型 | 创建 TaskSession，指定工具白名单 |
| 发送给子agent | 调度型 | 发送指令给子agent（被动执行一轮） |
| 停止子agent | 调度型 | 停止子agent |
| 删除子agent | 调度型 | 删除子agent |
| 发送到频道 | 通信型 | 发送消息到指定频道 |
| 检查通知 | 通信型 | 查看 TaskBridge 通知队列 |
| 设置关注规则 | 通信型 | 下发 WatchRule 到频道循环 |
| 检查任务队列 | 通信型 | 查看 TaskBridge 任务队列 |
| 便签板 | 自用型 | pin/unpin/list |
| 思考笔记 | 自用型 | write/delete key-value |
| 继续 | 自用型 | ContinueLoop=true 的空操作工具 |
| 读取文件 | 轻量执行 | 只读文件系统 |
| 记忆读写 | 轻量执行 | MemoryService 操作 |

**Review 专用工具**（ReviewEngine 内部管理）：

| 工具 | 类型 | 说明 |
|------|------|------|
| 检索记忆 | 执行型 | MemoryService.RecallAsync |
| 查看关联 | 执行型 | MemoryLinks.GetByMemoryIdAsync |
| 读取消息历史 | 执行型 | MessageRepository.GetByChannelAsync |
| 写入临时记忆 | 信号型 | → MemoryService.StoreAsync |
| 思考笔记 | 信号型 | write/delete key-value |
| 标记复盘 | 信号型 | → ReviewHintRepository.CreateAsync |
| 请求增援 | 信号型 | 备用预算并入总预算 |
| 保存进度 | 信号型 | → DreamProgress.Save |
| 完成 | 信号型 | 终止循环 |

---

## 用户系统

### Person/User 双层模型

- **Person**（自然人）— 持有 TrustLevel（信任等级 0-5）和 TrustProgress
- **User**（账号）— 持有 PermissionLevel（权限等级）、Platform、PlatformId、DisplayName、FastMemory

一个 Person 可关联多个 User（跨平台多账号）。Console 平台用户创建时自动设为 Admin 权限。

### TrustLevel（信任等级）

0=Unknown / 1=Stranger / 2=Understanding / 3=Familiarity / 4=Trust / 5=AbsoluteTrust

### PermissionLevel（权限等级）

Blocked / Restricted / Default / Elevated / Admin

权限检查在 WorkerEngineSpawnCheck 中前置拦截。

---

## 指令系统 (Command/)

### 接口

- **ICommand** — Name / Description / RequiredPermission / ExecuteAsync
- **IInteractiveCommand** — Steps(CommandStep 列表) / ExecuteInteractiveAsync（状态机 + 120s 超时）
- **CommandRegistry** — 静态注册表
- **CommandSpawnCheck** — 拦截命令前缀消息（Consumed 机制阻止进入 WorkerEngine）

### 命令清单

基础：/help /status /whoami
管理：/memory(list/search/delete/temp) /engine(list/stop) /wake /channel(list/affinity) /user(list/permission)
调试：/test recall /note /persona /trust /config /reload adapter
交互式：CommandSession 状态机，/cancel 退出

前缀可配置（CommandConfig.json）。

---

## 模型与 API (Models/)

- **Message** — Role / Content / ContentParts(多模态)
- **ContentPart** — Type(text/image) / Text / LocalPath
- **Usage** — PromptTokens / CompletionTokens / TotalTokens

---

## 工具类 (Util/)

### VectorUtil

- `CosineSimilarity(float[], float[])` — 余弦相似度
- `FloatsToBytes(float[])` / `BytesToFloats(byte[])` — 向量序列化（SQLite 存储用）

