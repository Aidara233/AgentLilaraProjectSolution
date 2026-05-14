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
      └── Plugin.BasicTools/  第一个插件（speak + send_media）
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
  ├── TaskBridge (频道循环 ↔ 系统循环异步通信)
  │     TaskQueue (重量请求: DelegateTask/RequestApproval/Escalate)
  │     NotificationQueue (轻量信号: Notify/ProgressUpdate/WatchHit)
  ├── DelegationRegistry (频道循环 → 系统循环委托生命周期管理)
  │     Submit → WaitForEvaluation → MarkExecuting → MarkCompleted/MarkFailed/MarkRetryPending
  │     RetryPending: 子agent失败后等待系统循环决策（MaxRetries=2）
  │     OnDelegationSubmitted → 唤醒系统循环
  │     OnDelegationCompleted → EventBus 信号唤醒源频道循环
  ├── ToolProfileManager (组件状态模型，链式继承)
  │     Profile: components(enabled/disabled/unavailable) + blockedTools/unblockedTools
  │     _root → channel/system/sub-agent 继承链
  │     channelMapping: channelId → profileName
  │     会话级组件激活: manage_components 工具
  ├── ComponentEventBus + GlobalComponentHost + ComponentHost(per-loop)
  │     Component 系统: IGlobalComponent(全局) / ILoopComponent(per-loop)
  │     ComponentRegistry: 类型注册，PluginLoader 扫描 [Component] 标记
  │     ComponentHost: 实例管理 + 工具注册/反注册 ToolRegistry
  └── 事件流水线: EventBus → HandleEventAsync
        ① 内核更新 (lastMessageTime)
        ② SpawnCheck: OnEventAsync → ShouldSpawnAsync → Create → StartEngine
        ③ 派发 OnEvent 给活跃实例
        ④ 清理 IsAlive=false 的实例
```

ISubEngine: EngineType / RunAsync / OnEvent / IsAlive / RequestStop / IsInfrastructure(默认false)
ISystemContext: 数据访问 + 适配器 + EventBus + 引擎查询 + StartEngine/RequestStopEngine + TaskBridge + Delegations + CurrentSleepState + MuteMode + NotifyChannel + ToolProfiles + ComponentEventBus + GlobalComponentHost + ComponentServices
IAgentSession: 统一会话接口 (ChannelSession/TaskSession/MonitorSession)

## 消息处理流

```
Adapter → EventBus(MessageEvent) → ChannelEngineSpawnCheck
  ① SessionManager.OnMessageAsync (用户映射 + 频道映射 + 消息入库)
  ② 权限检查 (Blocked/Restricted 拦截)
  ③ 按 ChannelId 路由: 有活跃 ChannelEngine → EnqueueMessage / 无 → 创建新 ChannelEngine
  （睡眠行为由 ChannelEngine 内部通过 IMessageInterceptor 插件处理，SpawnCheck 不拦截）

ChannelEngine (频道循环，常驻，一个活跃频道一个):
  闸门驱动循环 (LoopGate, auto-reset):
    gate.WaitAsync(coldTimeout) → 放行后立即重置 → 收集 → 执行 → 回到等待
    触发源: 缓冲定时器(新消息) / ContinueLoop自唤醒 / ESCALATE切模式
    超时 → 冷却退出

  内务模块体系 (EngineModule + LoopBus):
    SpeakModule / ThinkingNotesModule / TaskListModule / PinboardModule
    RetainListModule / MemoryWindowModule / LoopControlModule / SignalDispatchModule
    WatchRulesModule / DelegationModule / ToolStatusModule / SystemNotificationModule
    模块通过 LoopBus 订阅 ToolExecutedEvent 处理副作用
    模块通过 BuildPromptSection 注入 prompt（按 PromptPriority 排序）
  
  关注列表 (WatchRules):
    系统循环通过 SetWatchRuleTool 下发规则到频道循环
    规则含自主权级别: NotifyOnly / AutoRespond / Escalate
    频道循环在 Express prompt 注入规则，模型语义匹配
    命中后通过 TaskBridge.SendNotification 上报系统循环

  统一管线（主循环零分叉，每步根据 EngineMode 走不同实现）:
  ① WaitGate → 闸门放行
  ② CollectBuffer → drain 缓冲消息
  ③ PrepareContext → 每轮重建 contextXml（从DB拉最新历史）+ 记忆 + 授权
  ④ BuildPrompt → PromptBuilder（Express/Working 都走此路径）
  ⑤ CallModel → AgentCore.InvokeAsync（Express返回文本+Express工具，Working返回工具调用）
  ⑥ ProcessResponse → Express发文本+静默执行Express工具 / Working执行工具+发布事件
  ⑦ DecideNext → escalate工具(切模式+signal) / ContinueLoop(signal) / idle

  Express 工具 (fire-and-forget):
    ToolMetaAttribute.ExpressAvailable=true 标记的工具在 Express 模式下可用
    模型返回文本+tool_use → 文本发送，工具静默执行，结果不回注，不续轮
    核心 Express 工具: escalate(切Working) / manage_components(组件管理)
    非 native 提供商 fallback: 仍解析 [ESCALATE] 文本标记

  模式切换:
    Express escalate工具 → 切 Working + gate.Signal()，下轮自然走 Working
    Working 连续3次外部触发 → 回退 Express
    分类检测任务 → 直接进入 Working
  ⑧ ParseBotOutput 解析 <at/>/<reply/> 标签 → OutgoingMessage
  ⑨ MemoryExtractionCore 异步提取记忆 (每3条触发)
  ⑩ TrustProgress 每日自动增长 (per-person 日上限)

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
  闸门驱动循环 (LoopGate, auto-reset):
    gate.WaitAsync(coldTimeout) → 放行后立即重置 → 收集 → 执行 → 回到等待
    触发源: TaskBridge 任务/通知 / 委托提交 / ContinueLoop自唤醒 / 定时器
    超时 → 冷却退出
  
  容错与自愈:
    内层 Agent 循环异常: catch → 记录错误 → 退出当前轮（不杀外层 while）
    连续失败 ≥5 次: exponential backoff (10s→30s→60s→120s→300s)
    外层致命异常: IsAlive=false → SpawnCheck 检测到死亡 → 10s 后自动重启
    错误状态暴露: Snapshot 含 ConsecutiveFailures/TotalErrorCount/LastErrorMessage
  
  统一管线 (每轮重建上下文):
  ① WaitGate → 闸门放行
  ② CollectTasks → drain TaskBridge 任务队列和通知队列 + 待评估委托
  ③ PrepareContext → 重建上下文（活跃子agent/频道列表/任务队列/通知摘要/委托列表）
  ④ BuildPrompt → PromptBuilder（注入工具描述+上下文+便签板+思考笔记）
  ⑤ CallModel → AgentCore.InvokeAsync（返回工具调用）
  ⑥ ProcessResponse → 执行工具+发布事件
  ⑦ DecideNext → ContinueLoop(signal) / idle
  
  通信规则（不直接发消息）:
    系统循环不持有发消息能力，一律通过频道循环间接实现
    通知频道: NotifyChannel(channelId, content) → 注入频道循环 SystemNotificationModule
    委托结果: 子agent完成 → MarkCompleted → delegation-completed 信号 → 频道循环自动感知
    适配器操作: 拦截 send_* 类操作，仅允许查询类（获取群列表等）
  
  上下文持久化:
    每轮结束写入 Storage/SystemContext.json（WAL 模式）
    重启时恢复上下文（便签板/思考笔记/任务队列）
  
  上下文压缩:
    超过 MaxContextMessages(50) 触发压缩
    保留最近 10 条 + 压缩摘要（模型生成）
    压缩后写入持久化文件
  
  委托系统 (DelegationRegistry):
    频道循环 → DelegateTaskTool → 提交委托 → 唤醒系统循环
    系统循环 → EvaluateDelegationTool → accept/queue/reject
    accept → 创建 TaskSession(delegationId) → 执行 → MarkCompleted/MarkFailed
    完成 → EventBus 发 delegation-completed 信号 → 唤醒源频道循环
    频道循环 DelegationModule 注入结果到 prompt
    DelegateTaskTool 同步等待评估结果(15s超时)，返回 verdict 给频道循环
  
  子 agent 管理:
    通过 CreateSubAgentTool 创建 TaskSession（工具集从 sub-agent profile 获取）
    通过 StopSubAgentTool 停止子agent
    禁止套娃（子agent 不能创建子agent）
    失败恢复: TaskSession 内部 API 重试(max 3, 指数退避) → 失败标记 RetryPending + 双通知
    系统循环看到 RetryPending → 决定重试(IncrementRetry + 新建子agent) 或放弃(MarkFailed)
  
  工具集 (纯调度+轻量执行):
    调度类: CreateSubAgent / SendToSubAgent / StopSubAgent / DeleteSubAgent
    通信类: 通知频道 / CheckNotifications / SetWatchRule / CheckTaskQueue
    委托类: EvaluateDelegation（评估频道循环提交的委托）
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
  独立Agent循环 + 9个专用工具 + 4种复盘模式(频道日报/人物回顾/跨域关联/矛盾检测)
  工具调用: 支持原生 tool_use (ReviewCore.UseNativeTools) 和文本 JSON 双路径
  Token预算制: 基础预算 + 显式请求备用 + 预算外收尾
  产出写临时记忆 → 下次Consolidation整合
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

12张表: Person / User / Channel / UserMessage / MemoryEntry / TempMemoryEntry / MemoryLink / PersonaMemoryEntry / ReviewHint / ImageRecord / (Topics 保留但不再使用)

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
             IAgentMessaging / IDelegationAccess / ISubAgentAccess
             IChannelAccess / IAdapterAccess / ISchedulingAccess / IEngineAccess
             ISleepAccess / IEventBusAccess / IToolHistoryAccess / ILoopControl

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
  Plugin.WorkingTools    — pinboard + thinking_notes + retain_list（工作状态）[LoopComponent: working-tools]
  Plugin.MemoryTools     — memory（记忆读写，依赖 IMemoryAccess 服务）[GlobalComponent: memory-tools]
  Plugin.FileTools       — read_text + write_text + list_dir + move/delete/copy（文件系统）[GlobalComponent: file-tools]
  Plugin.DelegationTools — delegate_task + cancel_delegation（频道循环委托提交）[LoopComponent: delegation]
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
  实例化: 优先找 (IToolContext) 构造函数，其次无参构造
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

信任升级: Unknown→Stranger(首条消息) → Understanding(记忆数) → Familiarity(互动跨度) → Trust(模型评估)
实际等级 = min(硬性条件等级, TrustProgress允许等级)
TrustProgress 可负 → 等级被压到 Wary/Hostile

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
│     ├── DreamConfig.json    做梦调度参数+预算配置
│     ├── DreamStats.json     滚动7天统计+自适应基线
│     └── DreamProgress.json  复盘进度存档
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
├── Database/lilara.db        SQLite数据库
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
  LogStreamService — FrameworkLogger.OnLogWritten 事件 → 环形缓冲(2000条) → 实时推送
  快照方法: WorkerEngine.GetSnapshot() / DreamEngineSpawnCheck.GetDreamSnapshot()
           MasterEngine.GetSpawnCheck<T>() / GetActiveEnginesSnapshot()

页面:
  Dashboard    — 系统状态/引擎摘要/活跃Worker表格/做梦状态/实时日志尾部
  Logs         — 实时日志流 + 来源过滤 + 关键词搜索 + 暂停/恢复
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
  Signal.Begin → 新信号（适配器收消息、定时器、冲动触发）
  Signal.Continue → 继承信号（跨频道委托、子agent唤醒）
  Signal.Open/Close → 操作跨度（模型调用、工具执行、闸门开关）
  Signal.Event → 时间点日志
  信号吸收: 频道处理中收到新消息 → 双向记录 absorbed_by
```
