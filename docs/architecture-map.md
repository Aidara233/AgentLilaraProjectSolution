# Agent Lilara 架构地图

多平台 AI Agent 框架。核心：Engine 生态（OS 内核式调度）+ 记忆系统（双库 + 向量检索）+ 做梦机制（离线记忆整理 + 全局复盘）。

技术栈：.NET 8 / C# / SQLite-net-pcl / Anthropic SDK (Claude) / OpenAI SDK / SiliconFlow API / bge-large-zh-v1.5 embedding

## 目录结构

```
AgentLilaraProjectSolution/
├── AgentLilara.PluginSDK/   共享契约类库（ITool/IToolContext/服务接口），插件开发者引用此项目
├── AgentCoreProcessor/      主程序
│     ├── Adapter/     平台适配（File/OneBot QQ），消息收发，通用操作接口
│     │     ├── OneBot/  OneBotAdapter(协议层) + OneBotMessageParser(解析层) + OneBotActions(操作层) + OneBotConfig
│     │     ├── File/    FileAdapter（文件轮询，测试用）
│     │     └── 通用:    IAdapter / AdapterManager / AdapterFactory / AdapterStatus / AdapterAction
│     ├── Client/      IModelClient 抽象层（Claude/OpenAI 双协议）+ Embedding + IVisionProvider + IOcrProvider
│     ├── Command/     框架指令系统（/help /status /config 等）
│     ├── Config/      PathConfig 绝对路径管理
│     ├── Core/        业务核心（AgentCore+PreprocessingCore+MemoryExtractionCore+MemoryQueryCore+ConsolidationCore+ConsolidationFinalCore+WeightCore+LinkCore+CombineCore+DedupCore+ReviewCore+SummarizationCore+SleepTalkCore等），继承 CoreBase，各自 JSON 配置
│     ├── Database/    实体 + Repository（SQLite，13张表）
│     ├── Engine/      引擎生态（MasterEngine 内核 + 子引擎 + Worker闸门循环 + 内务模块）
│     ├── Memory/      MemoryService 检索管线
│     ├── MCP/         MCP Client 桥接层（外部插件生态接入）
│     ├── Tool/        工具宿主（PluginLoader/ToolRegistry/ToolExecutor/ToolProfileManager）+ 核心工具（continue_loop/wait）
│     ├── Util/        VectorUtil 向量操作
│     ├── WebUI/       Blazor Server 管理面板（嵌入式，同进程）
│     └── Program.cs   入口（WebApplication 宿主，默认启动 Web 服务器 + 适配器）
└── Plugins/
      ├── Plugin.BasicTools/      speak + send_media
      └── Plugin.CrossLoopTools/  跨循环委托与通信
```

## 引擎生态

```
MasterEngine (内核，实现 ISystemContext)
  ├── SpawnCheck 注册表 (IEngineSpawnCheck)
  │     Timer   → TimerEngine (心跳30s，IsInfrastructure=true)
  │     Channel → ChannelEngine (每活跃频道一个，常驻，频道循环)
  │     System  → SystemEngine (单例，系统循环，纯调度者)
  │     Dream   → DreamEngine (走神/小睡/大睡)
  │     Vision  → VisionEngine (图片描述+OCR，IsInfrastructure=true)
  │     Command → CommandSpawnCheck (指令拦截)
  ├── 活跃引擎表 (List<ISubEngine>)
  ├── CrossRequestRegistry (跨循环请求生命周期管理)
  │     Submit → Respond(Accept/Reject/Progress/Complete) → Idle → Archive
  │     广播: TargetId=null → 所有活跃循环见摘要
  │     定向: 指定TargetId → 目标循环见详情，自动激活或排队
  │     JSONL 追加持久化，重启恢复
  ├── DelegationBus (跨循环定向路由总线)
  │     引擎注册/注销 handler → Deliver 精准投递
  │     非全局广播，无关循环不感知
  ├── ToolProfileManager (组件状态模型，链式继承)
  │     Profile: components(enabled/disabled/unavailable) + blockedTools/unblockedTools
  │     _root → channel/system/sub-agent 继承链
  │     channelMapping: channelId → profileName
  │     会话级组件激活: manage_components 工具
  ├── GlobalComponentHost + ComponentHost(per-loop) + ModuleBus(per-loop)
  │     Component 系统: IGlobalComponent(全局) / ILoopComponent(per-loop)
  │     ComponentRegistry: 类型注册，PluginLoader 扫描 [Component] 标记
  │     ComponentHost: 实例管理 + 工具注册/反注册 ToolRegistry + ModuleBus 订阅
  │     ModuleBus: 每引擎独立 pub/sub（替代旧 ILoopBus + ComponentEventBus）
  └── 事件流水线: EventBus → HandleEventAsync
        ① 内核更新 (lastMessageTime)
        ② SpawnCheck: OnEventAsync → ShouldSpawnAsync → Create → StartEngine
        ③ 派发 OnEvent 给活跃实例
        ④ 清理 IsAlive=false 的实例
```

ISubEngine: EngineType / RunAsync / OnEvent / IsAlive / RequestStop / IsInfrastructure(默认false)
ISystemContext: 数据访问 + 适配器 + EventBus + 引擎查询 + StartEngine/RequestStopEngine + CrossRequests + DelegationBus + CurrentSleepState + MuteMode + ToolProfiles + GlobalComponentHost + ComponentServices
IAgentSession: 统一会话接口 (ChannelSession/TaskSession/MonitorSession)

### 引擎内部循环架构（Phase 1+2 统一模型）

```
每个引擎（Channel/System）内部结构:
  Gate (闸门)
    ├── delegate 驱动，组合不继承
    ├── WaitAsync(timeout) → 放行后立即重置
    └── Signal() 升闸（任何人可唤醒）

  Agent (多轮推理循环)
    ├── 构建上下文 → 调模型 → 执行工具 → 是否继续
    ├── 退避策略: 连续失败 → exponential backoff
    ├── OnToolExecuted 回调 → 宿主发布事件到总线
    ├── ConversationOffset: 区分框架注入和对话内容
    └── StopReason: Completed / MaxRounds / WaitRequested / ForceStopped / Error

  IAgentHost (宿主接口，引擎实现)
    ├── BuildStartInjectAsync(): 每次唤醒注入一次（固定前缀/摘要/记忆/新消息/组件目录）
    └── BuildRoundInjectAsync(): 每轮注入（实时状态/工具结果/信号缓冲）

  IInjectProvider (插件/组件注入接口)
    ├── BuildStartInjectAsync(InjectContext): 稳定快照，Start 时机
    ├── BuildRoundInjectAsync(InjectContext): 实时数据，每轮
    └── InjectPriority: 排序

  ModuleBus (每引擎独立 pub/sub)
    ├── Subscribe<T>(Action<T>): 订阅事件
    ├── Publish<T>(T): 发布事件
    └── 事件类型: ToolExecutedEvent / RoundEndingEvent / SpeakRequestedEvent / MemoryStoreEvent / SignalEmitEvent

  ChannelSignal (类型化信号缓冲)
    ├── NewMessageSignal: 新消息到达
    ├── BusEventSignal: EventBus 事件（委托完成等）
    ├── CompressionSignal: 压缩完成（新摘要 + 保留历史）
    └── ModeSwitchSignal: Express ↔ Working 切换

  CompressionTierModule (三层压缩)
    ├── L1 提示: 接近阈值时注入提示
    ├── L2 提醒: 超过软阈值时强提醒
    └── L3 硬保底: 超过硬阈值时强制压缩（模型调 compress 工具）

  ChannelContextPersistence (per-channel JSON 原子写入)
    ├── SaveContext(summary, mode, rounds): 只保存 ConversationOffset 之后的对话
    └── LoadContext(): 恢复摘要 + 模式 + 对话轮次
```

## 消息处理流

```
Adapter → EventBus(MessageEvent) → ChannelEngineSpawnCheck
  ① SessionManager.OnMessageAsync (用户映射 + 频道映射 + 消息入库)
  ② 权限检查 (Blocked/Restricted 拦截)
  ③ 按 ChannelId 路由: 有活跃 ChannelEngine → EnqueueMessage / 无 → 创建新 ChannelEngine
  （睡眠行为由 ChannelEngine 内部通过 IMessageInterceptor 插件处理，SpawnCheck 不拦截）

ChannelEngine (频道循环，常驻，一个活跃频道一个):
  实现 ISubEngine + IAgentHost

  闸门驱动循环 (Gate, auto-reset):
    gate.WaitAsync(coldTimeout) → 放行后立即重置 → 收集 → 执行 → 回到等待
    触发源: 缓冲定时器(新消息) / ContinueLoop自唤醒 / ESCALATE切模式 / EventBus信号
    超时 → 冷却退出

  堆叠式上下文模型:
    fixedPrefix: 系统配置 + 工具描述 + 人设 + 组件目录（BuildStartInjectAsync 注入）
    contextSummary: 压缩后的历史摘要（BuildStartInjectAsync 注入）
    记忆检索: BuildMemorySection → MemorySvc.RecallAsync（10s 超时，topK=5/10）
    IInjectProvider 收集: 组件 + 插件的 BuildStartInjectAsync/BuildRoundInjectAsync
    Agent.History: 追加式对话历史（ConversationOffset 之后为对话内容）

  Agent 循环 (Working 模式):
    EnsureAgent() → 创建 Agent + 恢复持久化上下文
    Agent.RunAsync(): BuildStartInject → [多轮: BuildRoundInject → 调模型 → 执行工具]
    OnToolExecuted → bus.Publish(ToolExecutedEvent) → 组件/模块响应
    StopReason: Completed(无工具) / WaitRequested(wait工具) / MaxRounds

  Express 模式 (直接调 Core，不走 Agent):
    ExecuteExpressCycleAsync: 构建上下文 → AgentCore.InvokeAsync → 发文本 + 静默执行 Express 工具
    Express 工具 (fire-and-forget): ToolMetaAttribute.ExpressAvailable=true
    核心 Express 工具: escalate(切Working) / manage_components(组件管理)
    非 native 提供商 fallback: 仍解析 [ESCALATE] 文本标记

  模式切换:
    Express escalate工具 → 切 Working + gate.Signal()，下轮自然走 Working
    Working 连续3次外部触发 → 回退 Express
    分类检测任务 → 直接进入 Working

  模块/组件体系:
    LoopControlModule: 轮次控制（MaxRounds/MaxSilentRounds）
    CompressionTierModule: 三层压缩（L1/L2/L3）
    ComponentHost + ModuleBus: 组件实例管理 + 事件订阅
    IInjectProvider 插件: Plugin.WorkingTools（pinboard/thinking_notes/retain_list/task_management）
    Plugin.BasicTools: speak + send_media（通过 ToolExecutedEvent 触发发送）

  关注列表 (WatchRules):
    系统循环通过 SetWatchRuleTool 下发规则到频道循环
    规则含自主权级别: NotifyOnly / AutoRespond / Escalate
    频道循环在 Express prompt 注入规则，模型语义匹配
    命中后通过 IAgentMessaging.SubmitFireAndForget 上报系统循环

  持久化 + 压缩:
    ChannelContextPersistence: 只保存 ConversationOffset 之后的对话内容
    CompressionSignal → ClearHistory + 新摘要 + fixedPrefix 重注入
    
  后处理:
    ParseBotOutput 解析 <at/>/<reply/> 标签 → OutgoingMessage
    MemoryExtractionCore 异步提取记忆 (每3条触发，独立 Worker)
    TrustProgress 每日自动增长 (per-person 日上限)

  图片处理 (ContextBuilder 图片感知):
    适配器层: [IMG:N] 占位符保留图片在消息中的位置
    ContextBuilder: 占位符 → <img id="N"/> 或 <img id="N" desc="..."/> 标记
    规则: new块普通图→直传, 表情包有描述→描述, history有描述→描述, 无描述→直传(受限)
    限制: 单张MaxDirectSendSize(1MB) / 总量MaxTotalDirectSendSize(3MB) / 数量MaxDirectSendCount(5)
    PromptBuilder: 文本XML + #IMGN:标签 + image block 混排
    "查看图片"工具: Working专用, 输入ImageRecord.Id, 返回原图
    缩略图: SkiaSharp 缩放(长边≤1568px) + JPEG 85%, 存 Storage/Images/thumbs/
```

VisionEngine (视觉引擎，单例，基础设施):
  闸门驱动循环 (LoopGate):
    触发源: 心跳(TimerEvent tick, 60s) / 新图信号(SignalEvent "new-image")
    ImageStorage 保存新图 → EventBus 发 "new-image" → SpawnCheck 转发 → gate.Signal()

  处理流程:
    小批次循环(BatchSize=10, ORDER BY CreatedAt DESC):
      每张图并行:
        ├── VisionWorker → IVisionProvider (Qwen3-VL-8B, SiliconFlow)
        └── OcrWorker → IOcrProvider (DeepSeek-OCR, SiliconFlow, 免费)
      两者独立写入 ImageRecord，互不等待
      每批完成后重新查询 → 新图自然插队

  并发控制:
    VisionSemaphore(default 3) — API 限流
    OcrSemaphore(default 4) — API 限流

  容错:
    Vision 401/403 → 暂停本轮所有 Vision 处理（下轮重试）
    OCR 失败 → 标记 HasText=false，记录错误
    SpawnCheck 检测死亡 → 10s 后自动重启

  配置: Storage/Engine/VisionEngineConfig.json
  模型配置: Storage/Core/VisionProvider.json + Storage/Core/OcrProvider.json

  WebUI:
    /engine/vision — 状态/配置/待处理队列/手动触发/全部重新生成
    /images — 图片库浏览/预览/筛选/编辑描述/删除

```

SystemEngine (系统循环，单例，纯调度者):
  实现 ISubEngine + IAgentHost

  闸门驱动循环 (Gate, auto-reset):
    gate.WaitAsync(coldTimeout) → 放行后立即重置 → 收集 → 执行 → 回到等待
    触发源: CrossRequests.OnRequestSubmitted / DelegationBus投递 / ContinueLoop自唤醒 / 定时器
    超时 → 冷却退出
  
  堆叠式上下文 (同 ChannelEngine 模型):
    BuildStartInjectAsync: 固定前缀 + 上下文摘要 + 状态快照 + IInjectProvider 收集
    BuildRoundInjectAsync: 实时状态 + 信号缓冲 drain + IInjectProvider 收集
    Agent 循环: 多轮推理 + OnToolExecuted → bus.Publish
    CompressionTierModule: 三层压缩（同频道循环）

  容错与自愈:
    内层 Agent 循环异常: catch → 记录错误 → 退出当前轮（不杀外层 while）
    连续失败 ≥5 次: exponential backoff (10s→30s→60s→120s→300s)
    外层致命异常: IsAlive=false → SpawnCheck 检测到死亡 → 10s 后自动重启
    错误状态暴露: Snapshot 含 ConsecutiveFailures/TotalErrorCount/LastErrorMessage
  
  通信规则（不直接发消息）:
    系统循环不持有发消息能力，一律通过频道循环间接实现
    通知频道: IAgentMessaging.SubmitFireAndForget → DelegationBus 路由 → 目标循环代理 prompt
    委托结果: 子agent完成 → OnCompleted回调 → CrossRequestRegistry.Respond(Complete) → 发起者自动唤醒
    适配器操作: 拦截 send_* 类操作，仅允许查询类（获取群列表等）
  
  上下文持久化:
    ContextPersistence 模块: Storage/SystemContext.json（WAL 模式）
    重启时恢复上下文（便签板/思考笔记/任务队列）
  
  上下文压缩:
    超过 80k tokens 触发（CompressionTierModule L1/L2/L3）
    模型调 compress 工具 → 生成摘要 → ClearHistory + 重注入
  
  
  模块/组件体系:
    LoopControlModule / PendingEventsModule
    ContextPersistence / ContextCompressionModule
    ComponentHost + ModuleBus: 组件实例管理
    IInjectProvider 插件: Plugin.WorkingTools（pinboard/thinking_notes/retain_list/task_management）

  工具集 (纯调度+轻量执行):
    调度类: CreateSubAgent / SendToSubAgent / StopSubAgent / DeleteSubAgent
    通信类: 通知频道 / CheckNotifications / SetWatchRule / CheckTaskQueue
    委托类: evaluate_request / complete_request（跨循环请求）
    自用类: 便签板 / 思考笔记 / 继续 / 等待
    轻量执行: 记忆读写 / 频道信息 / 引擎管理 / 适配器操作(仅查询)
```

## 冲动值决策 (per Channel, ImpulseConfig.json 全参数可配置)

```
私聊/控制台 → 缓冲聚合后一定回复
@提及(CQ at 或文本包含 botNames 或引用bot消息) → 穿透冷却期，必回
群聊 → 冲动值 ≥ 动态阈值:
  累加: BaseMessageScore × channelAffinity × participantFactor × ratioFactor
    participantFactor: 1人=1.0, 2人=0.9, 3人=0.8, 4+=0.6
    ratioFactor: clamp(reality / max(expectation, base), lower, upper)
  衰减: 每秒 -0.1 (线性)
  动态阈值: BaseThreshold + messageRate × ScaleFactor
    messageRate: EMA 跟踪消息频率，活跃频道阈值更高(bot占比更低)
  触发后: impulse -= threshold (不归零，保持活跃状态)
  发言后冷却 3s

EMA 社交满足度 (per Channel):
  expectation: bot主动发言 +2.0, 被@触发发言 +0.5
  reality: bot被@/引用/叫名字 +2.0 × trustMultiplier
  两者按时间指数衰减 (EmaDecayRate^elapsed)
  trustMultiplier 只影响 reality，不直接影响冲动值累加
```

## 做梦系统

```
三级睡眠:
  走神 — 随时，1片段 (Weight/Link)
  小睡 — 空闲600s，5片段 (Consolidation为主)
  大睡 — 红/黄评估+许可闸门，两阶段:
    Phase1(浅睡): 集中清临时记忆，Consolidation权重极高
    Phase2(深睡): 启动ReviewEngine + 并行跑Weight/Link/Combine

睡眠状态管理 (SleepState 枚举，ISystemContext.CurrentSleepState):
  DreamEngine 启动时设置，结束时重置为 None
  所有引擎可感知当前睡眠状态

分级打断 (DreamEngine.OnEvent):
  走神: 被@ → shouldWake=true，立即打断
  小睡: 被@ + 叫醒关键词("起床/醒醒/wake/起来/叫醒/别睡了/醒来") → 打断
         仅被@ → 触发梦话(SleepTalkCore)，不打断
  大睡: 消息不打断，仅 force-wake 信号可唤醒
         管理员@: ChannelEngineSpawnCheck 发 force-wake 信号
         任务提交: TaskBridge.OnTaskSubmitted 发 force-wake 信号

梦话 (SleepTalkCore):
  片段完成后概率触发: 大睡25% / 小睡15% / 走神不触发
  小睡被@时必定触发一条梦话
  内容: 基于当前片段 + 触发词生成梦幻呓语(≤50字)
  尊重 MuteMode

触发条件 (DreamEngineSpawnCheck):
  红色(任一触发): customRedAlert / 临时记忆爆仓 / 大睡间隔过长
  黄色(累计评分≥阈值): 空闲时长+距上次大睡+临时记忆比+未处理数+时间窗口
  → 决定锁定 → 许可闸门(管理员授权/超时自动) → 执行
  信号: force-sleep(触发睡觉) / force-wake(强制唤醒) / dream-config(配置更新)

五种片段: Consolidation / Weight / Link / Combine / Dedup
ReviewEngine (由DreamEngine孵化，不注册SpawnCheck):
  独立Agent循环 + 15个专用工具 + 自由探索模式（无固定目标）
  游标机制: focus(message_id/offset/channel_id) + browse(count) 正序阅读
  种子: 有信标→列出候选 / 无信标→随机频道 / 有进度→恢复
  评价系统: review_evaluate(++/+/0/-/--) → session内缓冲 → complete时取平均应用
    公式: delta = (boundary - current) * rate * averaged_coefficient
    人物4维度(reliability/respect/value/stability) + 频道1维度(value)
  Token预算制: ReviewConfig.json (基础50k + 备用15k + 压缩阈值30k)
  空转检测: 连续N轮只有导航没有行动 → 提醒
  信标来源: 工作端 mark_for_review 工具 + 框架自动(信任升级候选)
  信任升级: 多维度检查(EvaluationScore) + 硬性条件(天数/记忆数/review次数)
  日志: ReviewSessions + ReviewActions 表（业务级）+ Signal（技术级）
  进度持久化: ReviewProgress.json (游标/评价缓冲/笔记/token计数)
```

## 记忆系统

```
写入: MemoryExtractionCore 自动提取 / 工具"记忆"手动写入 / [REMEMBER]信号 → TempMemory (自动生成embedding)
  每条记忆含: Type(knowledge/fact/feedback/inference/event) + Subject(主题关键词) + PersonId/ChannelId(软标签)

检索: MemoryService.RecallAsync
  ① TempMemory 全量扫描 (小库，快)
  ② Memory 全量扫描 (person/channel 软加分，不做硬过滤)
  ③ 向量精排 (cosine similarity, MinRecallScore=0.25)
  ④ 关联扩展 (MemoryLink)
  ⑤ 综合排序: Similarity×0.5 + Importance×0.3 + Link×0.2 + TempBoost×0.1 + TagMatch×0.1
  ⑥ PersonaMemory 独立召回 (PersonaPenalty=0.05, PersonaMinScore=0.12, 仅聊天路径)
  
  增强检索 (MemoryQueryCore):
    轻量模型调用提取检索意图 (keywords + subjects)
    关键词命中加分 (KeywordBoost=0.15) + Subject匹配加分 (SubjectBoost=0.2)
    30s 意图缓存，同一轮对话内不重复调用

整合: TempMemory → Consolidation(做梦) → Memory
	去重: Dedup片段 → 关联MemoryLink 1-hop集群 → DedupCore模型判断merge/discard → 清理重复+重定向关联
	过期清理: 每次做梦RunAsync入口执行，DELETE IsPersistent=0 AND Expired + 孤立MemoryLink（纯SQL，无模型消耗）
标记: ReviewHint表 (工作时标记 → 复盘时消费)
低置信: Confidence(high/low) + Feedback(positive/negative)，低置信记忆标注"不太确定"
```

12张表: Person / User / Channel / UserMessage / MemoryEntry / TempMemoryEntry / MemoryLink / PersonaMemoryEntry / ReviewHint / ImageRecord / EvaluationScore / (Topics 保留但不再使用)
日志库(logs.db): events / token_usage
复盘库(主库): ReviewSessions / ReviewActions

## 工具系统（插件化架构）

```
AgentLilara.PluginSDK (共享契约，独立类库):
  ITool: Name / Description / Parameters / Timeout / ExecuteAsync / GetInputSchema()
  IToolContext: GetService<T>() / Storage
  IPluginStorage: GlobalDirectory / InstanceDirectory
  IPromptContributor: SectionKey / Priority / BuildSection()
  IMessageInterceptor: Priority / OnBeforeProcessAsync()
  ToolMetaAttribute: Group / ContinueLoop / AllowSubAgent / CapabilitySummary / Permission / Scope / ExpressAvailable
  ToolParameter: Name / Description / Index / IsRequired（控制 JSON Schema required 数组）
  EngineMode: Express / Working（SDK 枚举，供 ILoopControl 使用）
  Services/: IMemoryAccess（完整数据访问：语义搜索/向量操作/批量读取/临时库/关联图）
             IAgentMessaging / IChannelAccess / ISubAgentAccess
             IChannelAccess / IAdapterAccess / ISchedulingAccess / IEngineAccess
             ISleepAccess / IEventBusAccess / IToolHistoryAccess / ILoopControl
             IReviewAccess（游标/浏览/评价/笔记/进度）/ IReviewControl（预算/完成）
             IBeaconAccess（信标创建）/ IPersonAccess（人物查询/更新）

主程序 Tool/ (宿主层):
  Core/CoreTools.cs          — 核心工具（continue_loop + wait + manage_components + escalate），不可卸载
  Core/EscalateTool.cs       — Express→Working 模式切换工具（ExpressAvailable=true）
  Host/PluginLoader          — 扫描 {BaseDirectory}/Plugins/*.dll，AssemblyLoadContext 隔离加载
  Host/ToolContextImpl       — IToolContext 实现（ConcurrentDictionary 服务容器）
  Host/MemoryAccessImpl      — IMemoryAccess 桥接（Repository + EmbeddingProvider）
  Host/ToolProfileManager    — 组件状态模型（enabled/disabled/unavailable），链式继承，per-channel 映射
  ToolRegistry               — 全局注册表（Register/Unregister/Get/禁用管理）
  ToolExecutor               — 顺序执行 + 权限检查 + OnToolExecuted 回调

插件项目 (独立 DLL，输出到 {BaseDirectory}/Plugins/):
  Plugin.BasicTools      — speak + send_media（输出能力）[GlobalComponent: basic-tools]
  Plugin.WorkingTools    — pinboard + thinking_notes + retain_list + task_management + mark_for_review（工作状态+复盘标记）[LoopComponent: working-tools, IInjectProvider]
  Plugin.MemoryTools     — memory（记忆读写，依赖 IMemoryAccess 服务）[GlobalComponent: memory-tools]
  Plugin.FileTools       — read_text + write_text + list_dir + move/delete/copy（文件系统）[GlobalComponent: file-tools]
  Plugin.CrossLoopTools -- send_request + evaluate_request + complete_request + send_notify + report_progress + list_loops 等（跨循环委托与通信）[LoopComponent: cross-loop]
  Plugin.SystemTools     — evaluate_delegation + complete_delegation + notify_channel + create/stop_sub_agent（系统循环调度）[LoopComponent: system-ops]

ToolCall: 原生 tool_use (Claude API) 为主路径
  NativeToolCallHandler: 按 properties 顺序映射命名参数到位置输入
  参数名必须英文（Bedrock 代理不支持非 ASCII schema 属性名）

循环控制（两种模式统一为事件驱动）:
  频道循环: 每轮自动继续，模型调 wait 显式结束
    只说话不调其他工具 → 下一轮提示确认是否结束
    安全上限: MaxRounds=30(用户消息重置) / MaxSilentRounds=5(speak重置)
  系统循环: 处理完检查队列，有事继续，无事休眠
    事件驱动唤醒: TaskBridge/委托提交/定时器

消息拦截器 (IMessageInterceptor):
  插件可在引擎处理消息前介入（如睡眠行为、维护模式）
  按 Priority 升序执行，首个 Skip/Handled 短路后续

插件加载:
  目录: {程序目录}/Plugins/（跟程序走，不跟 Storage 走）
  每个 DLL 用独立 AssemblyLoadContext（支持卸载）
  多类型发现: ITool / IInjectProvider / IWebUIProvider / ILoopComponent / IGlobalComponent
  构造注入: EventBus / ModuleBus / Gate / IMemoryAccess / IServiceProvider
  延迟实例化: IInjectProvider/Component 由引擎创建（非全局单例），ITool 是全局单例
  启动时 MasterEngine.InitAsync 调用 PluginLoader.LoadAll()
  服务注入: ToolContext.Register<IMemoryAccess>(impl) 在插件加载前完成
```

## MCP 插件系统

```
MCP Client 集成（Model Context Protocol）:
  外部 MCP Server 的工具动态注册为 ITool，模型调用时与内置工具完全一致

架构:
  McpConfig          — Storage/MCP/McpServers.json 配置（Server 列表+工具覆盖）
  McpServerConnection — 单个 Server 连接生命周期（stdio/HTTP 传输）
  McpBridgeTool      — ITool 包装器（MCP 工具 → 内部工具接口桥接）
  McpServerManager   — 全局管理器（启动/停止/注册，MasterEngine.InitAsync 初始化）

参数映射:
  MCP: JSON Schema 命名参数 → ITool: 位置字符串数组
  发现时: required 参数在前 + 其余按字母序 → ToolParameter 列表
  调用时: 按索引映射回命名参数 Dictionary → MCP Server

工具命名: {toolPrefix}_{mcpToolName}（前缀可配置，避免与内置中文工具名冲突）
默认行为: ContinueLoop=true, RetainResult=true, 折叠组显示(DefaultExpanded=false)
权限: 服务器级默认 + 单工具覆盖（Default/Elevated/Admin）
传输: stdio（子进程）/ HTTP（Streamable HTTP/SSE）
容错: 单个 Server 连接失败不阻塞其他，MCP 整体失败不阻塞核心功能

配置: Storage/MCP/McpServers.json
  servers[].id/name/enabled/transport/command/args/env/url
  servers[].toolGroup/toolPrefix/permission/timeout
  servers[].toolOverrides.{toolName}.permission/continueLoop/retainResult

ToolRegistry 动态注册:
  Register(ITool) / Unregister(string) — ConcurrentDictionary 线程安全
  MCP 工具与内置工具共享 GenerateDescriptions/GenerateCapabilitySummary
```

## 用户系统

```
Person (自然人)          User (账号)
  Id                       Id
  TrustLevel (-2~5)        PersonId → Person
  TrustProgress (好感度)   Platform + PlatformId
  FastMemory (一句话概括)  PermissionLevel (Blocked~Admin)
  AlertLevel (0-4)         DisplayName
  LastAlertTime

TrustLevel: Hostile(-2) / Wary(-1) / Unknown(0) / Stranger(1) /
            Understanding(2) / Familiarity(3) / Trust(4) / AbsoluteTrust(5)

信任升级: Unknown→Stranger(消息数≥3) → Understanding(记忆≥5+天数≥3+任一维度≥8) → Familiarity(天数≥14+无警报+3/4维度≥20) → Trust(天数≥30+无近期警报+review≥3+全维度≥35)
降级: 维度跌破门槛即降（自动执行）
AbsoluteTrust: 仅管理员手动
评价存储: EvaluationScore 表 (TargetType/TargetId/Dimension/Value/LastEvaluatedAt)

报警按钮: alertLevel 递增惩罚(记录→扣分→限制+通知管理员)，冷却恢复(1/3/7/14天)
participants XML: <user name="..." relation="好友" memo="..."/>

一个Person可关联多个User (跨平台)
Console平台用户默认Admin权限
```

## 指令系统

```
ICommand / IInteractiveCommand 接口
CommandSpawnCheck 拦截 → CommandRegistry 路由
前缀可配置 (CommandConfig.json)
命令: /help /status /whoami /memory /engine /wake /channel /user
      /test recall /note /persona /trust /config /reload adapter /auth
交互式: CommandSession 状态机 + 120s超时
```

## 配置文件

```
Storage/
├── Core/*.json          各Core的LLM配置+系统提示词
├── Core/Persona.txt     共享人设（自动注入 UsePersona=true 的 Core）
├── Core/VisionProvider.json  视觉模型配置(apiKey/endpoint/model)
├── Core/OcrProvider.json     OCR模型配置(apiKey/endpoint/model)
├── Dream/
│     ├── DreamConfig.json    做梦调度参数+片段预算配置
│     ├── ReviewConfig.json   复盘引擎配置（评价参数/信任门槛/预算/空转检测）
│     ├── DreamStats.json     滚动7天统计+自适应基线
│     └── ReviewProgress.json 复盘进度存档（游标/评价缓冲/笔记）
├── Engine/
│     ├── EngineConfig.json          自启动引擎列表 {"autoStart":["Timer"]}
│     ├── ImpulseConfig.json         冲动值全参数配置
│     ├── TrustProgressionConfig.json 信任升降阈值+警报冷却配置
│     ├── VisionEngineConfig.json    视觉引擎并发/批量/重试配置
│     ├── ToolProfiles.json          组件配置（profile继承树+channelMapping）
│     └── ComponentConfig.json       组件启用/禁用状态
├── Adapter/
│     └── *.json  适配器实例配置（多实例，config-driven）
│         qq-main.json: OneBotAdapter 配置(WS地址/token/白名单/botNames)
├── Command/
│     └── CommandConfig.json  指令前缀配置
├── MCP/
│     └── McpServers.json     MCP Server 声明配置(传输/工具组/权限/覆盖)
├── PersonaMemorySeed.txt     人设记忆种子（首次启动导入）
├── Database/lilara.db        SQLite数据库（WAL模式，并行读写友好）
├── Logs/                     framework日志 + Model调用日志
└── WebUI/
      └── WebConfig.json      Web管理面板配置(端口/管理员账号)
```

## WebUI 管理面板

```
嵌入式 Blazor Server，同进程运行，默认启动。
端口配置: Storage/WebUI/WebConfig.json (默认 5000)
认证: Cookie Authentication，SHA256 密码哈希

数据桥接:
  SystemMonitor — 2s 周期采集 SystemSnapshot（引擎摘要/Worker快照/Dream状态）
  LogStreamService — 旧日志推送（待 Phase 3 迁移后移除）
  快照方法: WorkerEngine.GetSnapshot() / DreamEngineSpawnCheck.GetDreamSnapshot()
           MasterEngine.GetSpawnCheck<T>() / GetActiveEnginesSnapshot()

页面:
  Dashboard    — 系统状态/引擎摘要/活跃Worker表格/做梦状态/实时日志尾部
  Logs/Trace   — Signal 信号追踪（SVG渲染/虚拟化/实时推送/因果链高亮）
  Logs         — 旧实时日志流（待迁移到卡片系统后移除）
  Console      — 频道选择/创建 + 聊天式消息流 + 模拟用户/替Bot说话/自定义发送者 + @提及/私聊模拟
  EngineControl — 引擎启停/静音模式开关
  DreamControl  — 睡眠许可/强制睡觉/睡意偏移/红色警报
  WorkerDetail  — 单频道Worker完整状态（冲动值/EMA/轮次/授权工具）
  Messages     — 频道消息历史分页浏览 + 搜索
  Memories     — 主记忆/临时记忆浏览 + 人物/关键词过滤
  People       — 人物目录 + 信任等级 + 关联账号展开
  ConfigEditor — JSON配置组编辑器（类型感知输入/敏感字段遮罩）
  Config/Tools — 工具启用/禁用管理
  Config/Profiles — 组件配置管理（继承树+组件状态+工具屏蔽+频道映射）

启动行为:
  默认启动 Web 服务器（所有模式），ConsoleAdapter 已移除（仅 --debug 保留纯控制台）
  --debug / --test 模式不启动 Web 服务器
  ASP.NET 日志压制到 Warning 级别，控制台输出干净

卡片式数据驱动系统 (Phase 1+2 已实现):
  SDK 接口: IWebUIProvider / IDataSource / CardSchema(7种) / IPageContext / DataQuery
  Shell: ProviderRegistry + DynamicPage + CardGrid + CardHost + 7种卡片渲染器
  路由: /p/{route} → ProviderRegistry.FindPage → DynamicPage 渲染
  数据层: DataSourceManager (Fetch重试3次指数退避 + Subscribe推送)
  Provider 发现: PluginLoader 扫描 IWebUIProvider 实现类 (与 Component/ITool 同管道)
  热重载: ALC 卸载 → 反注册 → 重新加载 → 注册 → 侧边栏自动刷新
  卡片类型: Table / Status / Form / Stream / Chat / Tree / Detail (+ Custom 预留)
  布局: CSS Grid 12列，CardLayout 声明 PreferredCols/MinWidth/Height，移动端自动单列
```

## 日志系统（信号追踪）

```
Signal API (静态门面，开发者入口)
  ├── SignalContext (AsyncLocal 传播: signal_id / scope / branch / parent)
  ├── LogWriter (Channel<T> 有界队列 → 后台批量写入 → 通知订阅者)
  ├── LogDatabase (独立 SQLite: Storage/Database/logs.db, WAL 模式)
  │     ├── events 表 (signal_id, scope, branch, parent_id, span_id, group, level, type, timestamp, name, detail)
  │     └── token_usage 表 (从 Model close 事件派生)
  ├── OpenSpanTracker (内存 ConcurrentDictionary，快速查"当前卡在哪")
  ├── TokenAggregator (Model close → token_usage 聚合)
  └── LogQuery / LogAccessImpl (查询 + SDK ILogAccess 桥接)

SDK 接口: ISignalLogger (写) / ILogAccess (读写，继承 ISignalLogger)
兼容层: FrameworkLogger 保留旧 API，底层转发 Signal（过渡期，待 WebUI 迁移后移除）

信号模型:
  Signal.Begin → 新信号源◉（适配器收消息、Timer每tick独立信号）
  Signal.Continue → 继承上游信号（引擎生命周期/频道会话，带cause_span_id斜线）
  Signal.Open/Close → 操作跨度（闸门/轮次/模型调用/工具执行）
  Signal.Event/Error → 时间点日志/异常
  Close节点自动携带open的name（[完成] 处理轮次）
  SignalContext.Restore → Close后恢复父context，防止后续Open挂到已关闭span下

WebUI 日志追踪页 (/logs/trace):
  独立实现（不走卡片系统），LogTraceProvider 仅注册导航
  SVG 渲染引擎: slot分配 + 竖线/节点/斜线统一SVG
  虚拟化: 只渲染可视区±30行，rAF节流
  实时推送: 订阅 LogWriter 事件流
  交互: 悬停高亮+详情预览 / 单击锁定详情 / 双击筛选同信号 / 右键解锁
  文本区: 时间 / 来源(scope列) / 事件名 + 详情标签
  Close节点自动带open名称([完成] 处理轮次)
  列头中文显示(频道123/系统循环/Timer心跳等)
  悬停暂停自动滚动，滚轮恢复
```
