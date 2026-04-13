# Agent Lilara 架构文档

## 项目概览

Agent Lilara 是一个多平台 AI Agent 框架，接收来自不同平台的用户消息，经过分类、记忆检索、工具调用后，以人格化的方式回复用户。核心特色是"赛博睡眠"机制——空闲时自动进行离线记忆整理和全局复盘。

**技术栈**：.NET 8 / C# / SQLite-net-pcl / Newtonsoft.Json / DeepSeek-V3.2 / SiliconFlow API / bge-large-zh-v1.5 embedding

**目录结构**：

```
AgentCoreProcessor/
├── Adapter/          平台适配层（消息收发）
├── Client/           LLM API 客户端
├── Config/           路径配置
├── Core/             业务核心（13个 Core 类）
├── Database/         数据实体 + Repository
├── Engine/           引擎生态（调度中心）
├── Memory/           记忆服务
├── Models/           API 数据模型
├── Tool/             工具系统
├── Util/             工具类
└── Program.cs        入口
```

---

## 适配器层 (Adapter/)

纯粹的消息收发适配层，不含业务逻辑。

### 接口与管理

- **IAdapter** — 统一接口：`StartAsync()` / `SendMessageAsync(OutgoingMessage)` / `OnMessageReceived` 事件
- **AdapterManager** — 管理所有 Adapter 实例，提供按平台名发送的统一入口 `SendMessageAsync(platform, message)`

### 实现

- **ConsoleAdapter** — 控制台交互，开发调试用
- **FileAdapter** — 轮询 `input.txt` 读取消息、输出追加到 `output.txt`，用于自动化测试。`--file` 参数启用

### 消息格式

- **IncomingMessage** — Platform / PlatformUserId / ChannelId / Content / ReplyToMessageId
- **OutgoingMessage** — ChannelId / Content

Adapter 接收平台消息 → 转为 IncomingMessage → 通过 EventBus 发布 MessageEvent。

---

## API 客户端 (Client/)

### AIApiClient

封装 HTTP 请求和 SSE 流式解析，对接 OpenAI 兼容 API。支持：
- 流式生成（SSE）
- 对话历史管理（ConversationHistory）
- 预设消息（PresetMessages，系统提示词）

### ApiClientCfg

JSON 配置类，每个 Core 对应一个配置文件 `Storage/Core/{CoreName}.json`：
- apiKey / apiEndpoint / model
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
- **Processor** — Core 与 AIApiClient 的桥梁，加载配置并发起请求
- **GenerateAsync()** — 流式生成，支持 `<over>` break 检测，返回 Usage（token 统计）
- **GenerateOnceAsync()** — 一次性生成，返回完整文本
- **ResetProcessor()** — 重置处理器（清除对话历史，重新应用 extraMessage）
- **LogOutput()** — 写入 `Storage/Logs/Model/{timestamp}_{CoreName}.log`

### PreprocessingCore

消息分类路由。输入用户消息，输出分类数字 1-4：
1. 简单聊天
2. 需要额外知识的聊天
3. 需要工具的任务
4. 大型复杂任务

配置：thinking 禁用以保持快速响应。

### ExpressCore

人格化输出，双模式：
- **PolishAsync(userRequest, rawText)** — 润色模式：带用户原始需求上下文，润色 WorkingCore 的说话内容
- **GenerateOnceAsync(input)** — 自由模式：直接聊天回复

### WorkingCore

Agent 循环的中心——通过多轮工具调用完成任务。

**循环结构**（最多 10 轮）：
1. PromptBuilder 组装本轮消息
2. GenerateAsync 调用模型，通过 `<over>` break 解析 ToolCall JSON
3. ToolExecutor DAG 执行
4. 处理特殊工具副作用（完成/思考笔记/说话/记忆/信号）
5. 收集 retain=true 结果
6. 更新滚动状态，进入下一轮

**跨轮状态**：
- `register` — toolId → 输出数据（寄存器，跨轮持久）
- `thinkingNotes` — key → value（思考笔记）
- `retainedResults` — retain=true 的历史结果

**回调机制**（由 WorkerEngine 在调用前设置）：
- `OnSpeak` — 说话 → ExpressCore 润色 + Adapter 推送
- `OnMemory` — 记忆 → MemoryService.StoreAsync
- `OnSignal` — 信号 → EventBus.PublishSignal
- `OnReviewHint` — 标记复盘 → ReviewHintRepository.CreateAsync

### PromptBuilder

每轮动态组装消息列表，注入顺序：
1. 工具描述（ToolRegistry 动态生成）
2. 附加上下文（记忆注入点）
3. 用户原始需求
4. 思考笔记
5. 历史保留结果（retain=true）
6. 上一轮工具执行结果

### 话题管理

- **TopicClassificationCore** — 三层分类：规则层(回复引用) → 向量层(Summary embedding + 时间衰减) → 模型层(兜底)
- **TopicSummaryCore** — 创建时生成初始摘要，每 5 条消息自动更新，embedding 存入 Topic 表

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
| User | Users | Id, PersonId→Person, Platform, PlatformId, PermissionLevel, FastMemory |
| Channel | Channels | Id, Name |
| Topic | Topics | Id, ChannelId, Name, Summary, Embedding, MessageCount, IsActive, LastMessageTime |
| UserMessage | UserMessages | Id, UserId, ChannelId, TopicId, Content, Time |
| MemoryEntry | Memories | Id, PersonId?, ChannelId?, TopicId?, Content, Embedding, Importance, IsDerived, SourceHash, LastDreamTime, IsPersistent, ExpiresAt |
| TempMemoryEntry | TempMemories | Id, PersonId?, ChannelId?, TopicId?, Content, Embedding, SourceMessageId |
| MemoryLink | MemoryLinks | Id, SourceId, TargetId, Strength, LinkType |
| ReviewHint | ReviewHints | Id, Content, PersonId?, ChannelId?, TopicId?, IsProcessed |

### 关键 Repository 方法

**MemoryRepository**：GetRecentAsync / GetByPersonAsync / GetByTagsAsync / GetUndreamedAsync / GetOldestDreamedAsync / GetBySourceHashAsync / CreateAsync / CreateDerivedAsync / UpdateAsync

**TempMemoryRepository**：GetAllAsync / GetByTagsAsync / CreateAsync / DeleteAsync / ClearAllAsync

**MemoryLinkRepository**：GetLinksForAsync(ids, minStrength) / GetByMemoryIdAsync / CreateOrUpdateAsync

**ReviewHintRepository**：GetUnprocessedAsync / MarkProcessedAsync / CreateAsync / DeleteProcessedAsync

**MessageRepository**：GetByTopicAsync / GetRecentByTopicAsync / GetByChannelAsync / SaveAsync

**TopicRepository**：GetActiveByChannelAsync / GetActiveWithEmbeddingAsync / CreateAsync / DeactivateStaleAsync

**PersonRepository**：GetAllUserIdsAsync(personId) / MergeAsync(target, source)

---

## 引擎生态 (Engine/)

MasterEngine 是纯"操作系统内核"，不含任何子引擎的业务逻辑。子引擎自治，新增引擎类型不改主引擎代码。

### 三个核心接口

**ISubEngine**（引擎实例）：
- `EngineType` — 类型标识
- `RunAsync()` — 主执行逻辑，结束后 IsAlive 变 false
- `OnEvent(EngineEvent)` — 接收事件
- `IsAlive` — 是否仍在运行
- `RequestStop()` — 请求停止
- `IsInfrastructure` — 默认 false，true 表示基础设施引擎（不影响 IsIdle 判定）

**IEngineSpawnCheck**（引擎类型注册，每种类型一个常驻实例）：
- `OnEvent(e, ctx)` — 处理事件，更新内部状态
- `ShouldSpawn(e, ctx)` — 是否需要创建新实例
- `Create(ctx)` — 创建实例

**ISystemContext**（MasterEngine 暴露的"系统调用"）：
- 数据访问：Memories / TempMemories / MemoryLinks / ReviewHints / MemorySvc / Session / Embedding
- 适配器：Adapters
- 事件：EventBus
- 引擎查询：IsIdle / IdleDuration / LastMessageTime / HasActiveEngine / GetActiveEngineCount
- 引擎管理：StartEngine(engine) / RequestStopEngine(engine)

### MasterEngine

实现 ISystemContext。核心职责：

**InitAsync**：初始化 DB → 创建 Repository → 注册 SpawnCheck → 订阅 EventBus → 加载 EngineConfig.json 自启动引擎

**HandleEventAsync**（事件流水线）：
1. 内核更新（MessageEvent → 更新 lastMessageTime）
2. 遍历 SpawnCheck：OnEvent → ShouldSpawn → Create → StartEngine
3. 派发 OnEvent 给所有活跃实例
4. 清理 IsAlive=false 的实例

**SpawnCheck 工厂**：
```csharp
["Timer"]  = () => new TimerEngineSpawnCheck(),
["Worker"] = () => new WorkerEngineSpawnCheck(),
["Dream"]  = () => new DreamEngineSpawnCheck(),
```

**IsIdle**：排除 IsInfrastructure=true 的引擎后，无活跃引擎即为空闲。

### TimerEngine

心跳引擎。`IsInfrastructure = true`。RunAsync 循环：delay(30s) → EventBus.Publish(TimerEvent("tick"))。通过 EngineConfig.json autoStart 自启动。可响应 "timer-interval" 信号调整间隔。

### WorkerEngine

每条消息创建一个实例。RunAsync 流程：
1. SessionManager.OnMessageAsync — 用户映射 + 话题分类 + 消息入库
2. 权限检查（Blocked/Restricted 拦截）
3. PreprocessingCore 分类
4. MemoryService.RecallAsync 检索记忆
5. 路由：1-2 级 → ExpressCore / 3-4 级 → WorkingCore Agent 循环

WorkerEngineSpawnCheck：MessageEvent → ShouldSpawn = true。

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

持有所有跨睡眠周期的状态和做梦调度逻辑。

**信号处理**（OnEvent）：
- dream-permission → 授予许可
- force-sleep → 强制大睡（跳过一切评估）
- sleep-score-offset → 调整睡意偏移（负值解除决定锁定）
- red-alert → 触发红色警报
- dream-config → 运行时更新配置
- TickEvent → 更新统计快照

**触发条件**（ShouldSpawn，仅在 TickEvent 时评估）：
1. force-sleep → 立即大睡
2. 已锁定（permissionRequestTime != null）→ 只等许可
3. 红色评估（任一触发）：customRedAlert / 临时记忆 > 基线×倍率 / 距上次大睡过久
4. 黄色评估（累计评分 ≥ 阈值）：空闲时长 + 距上次大睡 + 临时记忆/基线比 + 未处理记忆数 + 时间窗口评分
5. 小睡：空闲 > 600s
6. 走神：冷却到期（120s）

**决定锁定**：评估达标后设 permissionRequestTime，后续周期不重新评估，只等许可。防止"管理员授权了但分数飘回阈值以下"。

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

会话管理器，负责消息的 Topic 归类、用户/频道映射、消息入库。

**OnMessageAsync** 流程：
1. 平台用户 → 内部 User（自动创建 Person）
2. 频道查找/创建
3. 话题分类（三层）：规则层(回复引用) → 向量层(embedding 相似度 + 时间衰减) → 模型层(TopicClassificationCore)
4. 消息入库
5. 每 5 条消息异步更新 Topic Summary + embedding

**向量层参数**：Similarity×0.7 + Recency×0.3，高置信 0.8 / 低置信 0.5，模糊区间触发模型确认。

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
2. 主库标签过滤（PersonId / ChannelId / TopicId）
3. 向量精排（cosine similarity）
4. 关联扩展（MemoryLink，可选）
5. 综合排序：Similarity×0.5 + Importance×0.3 + Link×0.2 + TempBoost×0.1
6. 返回 top-K，更新 LastAccessedAt

### 双库架构

- **TempMemory**（临时库）— 缓冲区，写入快，做梦时由 Consolidation 整合入主库
- **Memory**（主库）— 持久存储，带 Importance 权重、关联网络、做梦元数据

### 做梦整合路径

```
用户消息 → 工具"记忆" → TempMemory
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

- **ITool** — Name / Description / Parameters / Timeout / ExecuteAsync(resolvedInputs, ct) → ToolResult
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

**全局工具**（ToolRegistry，WorkerEngine 的 Agent 循环用）：

| 工具 | 类型 | 说明 |
|------|------|------|
| 文件流读取器 | 执行型 | 读文件返回内容 |
| 文件流写入器 | 执行型 | 写文件，支持 ref 输入 |
| 说话 | 信号型 | → OnSpeak 回调 |
| 完成 | 信号型 | 终止 Agent 循环 |
| 思考笔记 | 信号型 | write/delete key-value |
| 记忆 | 信号型 | → OnMemory 回调 |
| 睡眠许可 | 信号型 | → EventBus "dream-permission" |
| 强制睡觉 | 信号型 | → EventBus "force-sleep" |
| 修改睡眠配置 | 信号型 | → EventBus "dream-config" |
| 调整睡意 | 信号型 | → EventBus "sleep-score-offset" |
| 触发红色警报 | 信号型 | → EventBus "red-alert" |
| 标记复盘 | 信号型 | → OnReviewHint 回调 |

**Review 专用工具**（ReviewEngine 内部管理）：

| 工具 | 类型 | 说明 |
|------|------|------|
| 检索记忆 | 执行型 | MemoryService.RecallAsync |
| 查看关联 | 执行型 | MemoryLinks.GetByMemoryIdAsync |
| 读取消息历史 | 执行型 | MessageRepository.GetByTopicAsync |
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
- **User**（账号）— 持有 PermissionLevel（权限等级）、Platform、PlatformId、FastMemory

一个 Person 可关联多个 User（跨平台多账号）。Console 平台用户创建时自动设为 Admin 权限。

### TrustLevel（信任等级）

0=Unknown / 1=Stranger / 2=Understanding / 3=Familiarity / 4=Trust / 5=AbsoluteTrust

### PermissionLevel（权限等级）

Blocked / Restricted / Default / Elevated / Admin

权限检查在 WorkerEngine.RunAsync 中前置拦截。

---

## 模型与 API (Models/)

- **Message** — Role / Content
- **ApiRequest** — 构建 LLM API 请求体
- **ApiResponse** — 解析流式响应（Choices / Delta / Usage）
- **Usage** — PromptTokens / CompletionTokens / TotalTokens

---

## 工具类 (Util/)

### VectorUtil

- `CosineSimilarity(float[], float[])` — 余弦相似度
- `FloatsToBytes(float[])` / `BytesToFloats(byte[])` — 向量序列化（SQLite 存储用）

