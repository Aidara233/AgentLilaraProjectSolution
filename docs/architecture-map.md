# Agent Lilara 架构地图

多平台 AI Agent 框架。核心：Engine 生态（OS 内核式调度）+ 记忆系统（双库 + 向量检索）+ 做梦机制（离线记忆整理 + 全局复盘）。

技术栈：.NET 8 / C# / SQLite-net-pcl / Anthropic SDK (Claude) / OpenAI SDK / SiliconFlow API / bge-large-zh-v1.5 embedding

## 目录结构

```
AgentCoreProcessor/
├── Adapter/     平台适配（Console/File/OneBot QQ），消息收发
├── Client/      IModelClient 抽象层（Claude/OpenAI 双协议）+ Embedding 接口
├── Command/     框架指令系统（/help /status /config 等）
├── Config/      PathConfig 绝对路径管理
├── Core/        业务核心（AgentCore统一+PreprocessingCore+MemoryExtractionCore等），继承 CoreBase，各自 JSON 配置
├── Database/    实体 + Repository（SQLite，12张表）
├── Engine/      引擎生态（MasterEngine 内核 + 子引擎 + Worker闸门循环 + 内务模块）
├── Memory/      MemoryService 检索管线
├── Tool/        工具接口 + 顺序执行器 + 工具折叠分组 + 全局/局部工具集
├── Util/        VectorUtil 向量操作
├── WebUI/       Blazor Server 管理面板（嵌入式，同进程）
│     ├── Services/    SystemMonitor(快照采集) + LogStreamService(日志流) + WebConfig + WebAuthService
│     ├── Components/  Razor 页面（Dashboard/Logs/EngineControl/DreamControl/WorkerDetail/Messages/Memories/People/ConfigEditor/Login）
│     └── wwwroot/     静态资源（Bootstrap 5 CSS）
└── Program.cs   入口（WebApplication 宿主，默认启动 Web 服务器 + 适配器）
```

## 引擎生态

```
MasterEngine (内核，实现 ISystemContext)
  ├── SpawnCheck 注册表 (IEngineSpawnCheck)
  │     Timer   → TimerEngine (心跳30s，IsInfrastructure=true)
  │     Worker  → WorkerEngine (每活跃频道一个，常驻)
  │     Dream   → DreamEngine (走神/小睡/大睡)
  │     Command → CommandSpawnCheck (指令拦截)
  ├── 活跃引擎表 (List<ISubEngine>)
  └── 事件流水线: EventBus → HandleEventAsync
        ① 内核更新 (lastMessageTime)
        ② SpawnCheck: OnEventAsync → ShouldSpawnAsync → Create → StartEngine
        ③ 派发 OnEvent 给活跃实例
        ④ 清理 IsAlive=false 的实例
```

ISubEngine: EngineType / RunAsync / OnEvent / IsAlive / RequestStop / IsInfrastructure(默认false)
ISystemContext: 数据访问 + 适配器 + EventBus + 引擎查询 + StartEngine/RequestStopEngine

## 消息处理流

```
Adapter → EventBus(MessageEvent) → WorkerEngineSpawnCheck
  ① SessionManager.OnMessageAsync (用户映射 + 频道映射 + 消息入库)
  ② 权限检查 (Blocked/Restricted 拦截)
  ③ 按 ChannelId 路由: 有活跃 Worker → EnqueueMessage / 无 → 创建新 Worker

WorkerEngine (常驻，一个活跃频道一个):
  闸门驱动循环 (LoopGate, auto-reset):
    gate.WaitAsync(coldTimeout) → 放行后立即重置 → 收集 → 执行 → 回到等待
    触发源: 缓冲定时器(新消息) / ContinueLoop自唤醒 / ESCALATE切模式
    超时 → 冷却退出

  内务模块体系 (EngineModule + LoopBus):
    SpeakModule / ThinkingNotesModule / TaskListModule / PinboardModule
    RetainListModule / MemoryWindowModule / LoopControlModule / SignalDispatchModule
    模块通过 LoopBus 订阅 ToolExecutedEvent 处理副作用
    模块通过 BuildPromptSection 注入 prompt（按 PromptPriority 排序）

  统一管线（主循环零分叉，每步根据 EngineMode 走不同实现）:
  ① WaitGate → 闸门放行
  ② CollectBuffer → drain 缓冲消息
  ③ PrepareContext → 每轮重建 contextXml（从DB拉最新历史）+ 记忆 + 授权
  ④ BuildPrompt → PromptBuilder（Express/Working 都走此路径）
  ⑤ CallModel → AgentCore.InvokeAsync（Express返回文本，Working返回工具调用）
  ⑥ ProcessResponse → Express发文本 / Working执行工具+发布事件
  ⑦ DecideNext → ESCALATE(切模式+signal) / ContinueLoop(signal) / idle

  模式切换:
    Express [ESCALATE] → 切 Working + gate.Signal()，下轮自然走 Working
    Working 连续3次外部触发 → 回退 Express
    分类检测任务 → 直接进入 Working
  ⑧ ParseBotOutput 解析 <at/>/<reply/> 标签 → OutgoingMessage
  ⑨ MemoryExtractionCore 异步提取记忆 (每3条触发)
  ⑩ TrustProgress 每日自动增长 (per-person 日上限)
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

触发条件 (DreamEngineSpawnCheck):
  红色(任一触发): customRedAlert / 临时记忆爆仓 / 大睡间隔过长
  黄色(累计评分≥阈值): 空闲时长+距上次大睡+临时记忆比+未处理数+时间窗口
  → 决定锁定 → 许可闸门(管理员授权/超时自动) → 执行

四种片段: Consolidation / Weight / Link / Combine
ReviewEngine (由DreamEngine孵化，不注册SpawnCheck):
  独立Agent循环 + 9个专用工具 + 4种复盘模式(频道日报/人物回顾/跨域关联/矛盾检测)
  Token预算制: 基础预算 + 显式请求备用 + 预算外收尾
  产出写临时记忆 → 下次Consolidation整合
```

## 记忆系统

```
写入: MemoryExtractionCore 自动提取 / 工具"记忆"手动写入 → TempMemory (自动生成embedding)
检索: MemoryService.RecallAsync
  ① TempMemory 全量扫描 (小库，快)
  ② Memory 标签 OR 过滤 (Person/Channel，任一匹配即召回)
  ③ 向量精排 (cosine similarity, MinRecallScore=0.25)
  ④ 关联扩展 (MemoryLink)
  ⑤ 综合排序: Similarity×0.5 + Importance×0.3 + Link×0.2 + TempBoost×0.1
  ⑥ PersonaMemory 独立召回 (PersonaPenalty=0.05, PersonaMinScore=0.12, 仅聊天路径)
整合: TempMemory → Consolidation(做梦) → Memory
标记: ReviewHint表 (工作时标记 → 复盘时消费)
低置信: Confidence(high/low) + Feedback(positive/negative)，低置信记忆标注"不太确定"
```

12张表: Person / User / Channel / UserMessage / MemoryEntry / TempMemoryEntry / MemoryLink / PersonaMemoryEntry / ReviewHint / ImageRecord / (Topics 保留但不再使用)

## 工具系统

```
ITool: Name / Description / Parameters / Timeout / ExecuteAsync
       AllowSubAgent(默认true) / RequiredPermission(默认Default)
       ContinueLoop(默认false) / RetainResult(默认false) / CapabilitySummary(默认null)
       ToolGroup(默认null=始终可见) / DefaultExpanded(默认true)

ToolCall(JSON): {"tool": "工具名", "inputs": ["参数1", "参数2"]}
ToolExecutor: 顺序执行 + 预授权检查(查权限表，不阻塞) + OnToolExecuted回调

事件驱动循环 (WorkerEngine 闸门模型):
  ContinueLoop=true 的工具被调用 → gate.Signal() 自唤醒下一轮
  全部 ContinueLoop=false → 自然 idle，等待外部事件
  不需要显式"完成"工具

工具折叠 (ToolGroup):
  默认组(null): 始终可见
  展开组(DefaultExpanded=true): 文件操作、远程终端
  折叠组(DefaultExpanded=false): 系统管理
  元工具「激活工具组」: 运行时展开折叠组
  折叠组在 prompt 中只显示一行摘要

便签板 (Pinboard):
  会话级上下文注入，Express/Working 共享
  Working 通过 PinboardTool 操作(pin/unpin/list)，Express 只读
  每轮 prompt 全量展示内容

缓存列表 (Retain):
  RetainResult=true 的工具结果自动收集（摘要+完整内容）
  prompt 只注入摘要，模型通过 RetainListTool 的 view 查看完整内容
  支持 remove/clear 管理

继续工具:
  ContinueLoop=true 的空操作工具
  便签板等非 ContinueLoop 工具操作后想继续工作时使用

预授权模型:
  ITool.RequiredPermission > Default 的工具为受限工具
  管理员通过 /auth grant <工具名> 预授权（频道级）
  ToolExecutor 查权限表：有权限执行，无权限返回提示
  不阻塞工具执行流程

Express/Working 自适应切换:
  默认 Express 模式（轻量聊天）
  ExpressCore 输出 [ESCALATE] → 切换到 Working 模式
  Working 模式下连续 3 次外部消息触发 → 回退到 Express
  CapabilitySummary 自动注入 Express prompt（模型知道可升级的能力）

全局工具 (ToolRegistry):
  自由工具(Default): 说话 / 思考笔记 / 记忆 / 标记复盘 / 任务管理 / 报警 / 读取文件 / 便签板 / 缓存管理 / 继续
  受限工具(Elevated): 睡眠许可 / 强制睡觉 / 调整睡意 / 远程终端 / 写入文件 / 文件传输
  受限工具(Admin): 修改睡眠配置 / 触发红色警报
  已禁用: 委派任务 / 查看子agent (代码已删除)

文件系统沙盒:
  Storage/Workspace/ — 自由工作区（500MB上限）
  Storage/ 其余 — 受限区（写入需 Elevated）
  Storage/ 之外 — 完全禁止
  黑名单: lilara.db / SSH目录 / *.key 文件
  FileTransferTool: SCP 双向传输（主机↔Alpine VM，10MB上限）

Review专用工具 (ReviewEngine内部):
  检索记忆 / 查看关联 / 读取消息历史 / 更新亲和度 / 写入临时记忆 / 思考笔记
  更新快速记忆 / 调整好感度 / 标记复盘 / 请求增援 / 保存进度 / 完成
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
├── Dream/
│     ├── DreamConfig.json    做梦调度参数+预算配置
│     ├── DreamStats.json     滚动7天统计+自适应基线
│     └── DreamProgress.json  复盘进度存档
├── Engine/
│     ├── EngineConfig.json          自启动引擎列表 {"autoStart":["Timer"]}
│     ├── ImpulseConfig.json         冲动值全参数配置
│     └── TrustProgressionConfig.json 信任升降阈值+警报冷却配置
├── Adapter/
│     └── OneBotAdapter.json  QQ适配器配置(WS地址/白名单/botNames)
├── Command/
│     └── CommandConfig.json  指令前缀配置
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
  EngineControl — 引擎启停/静音模式开关
  DreamControl  — 睡眠许可/强制睡觉/睡意偏移/红色警报
  WorkerDetail  — 单频道Worker完整状态（冲动值/EMA/轮次/授权工具）
  Messages     — 频道消息历史分页浏览 + 搜索
  Memories     — 主记忆/临时记忆浏览 + 人物/关键词过滤
  People       — 人物目录 + 信任等级 + 关联账号展开
  ConfigEditor — JSON配置组编辑器（类型感知输入/敏感字段遮罩）
```
