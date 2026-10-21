# Agent Lilara 架构地图

多平台 AI Agent 框架。会话体系：频道响应已全面会话化（express/working/task 都是 RuntimeSession，由 SessionEngine 的资源池树管理），基础设施引擎层（ISubEngine）+ 会话层（RuntimeSession 统一 agent 循环）。

技术栈：.NET 8 / C# / SQLite-net-pcl / Anthropic SDK (Claude) / OpenAI SDK / SiliconFlow API / bge-large-zh-v1.5 embedding

## ALP → ALC 内核统一迁移

统一方向已确认：**ALC 内核 + ALP 宿主**。ALP 保留频道/插件/Mind/WebUI/SQLite 等领域宿主能力；ALC 提供 canonical 时间线、Stepper 与上下文投影。迁移边界、单写者规则、基线、阶段和验收条件见 [`kernel-integration/00-boundary-adr.md`](kernel-integration/00-boundary-adr.md)。迁移按阶段单独审批，当前不启用运行路径。

## 目录结构

```
AgentLilaraProjectSolution/
├── AgentLilara.PluginSDK/   共享契约类库（ITool/IToolContext/服务接口），插件开发者引用此项目
├── AgentCoreProcessor/      主程序
│     ├── Adapter/     平台适配（File/OneBot QQ），消息收发，通用操作接口
│     │     ├── OneBot/  OneBotAdapter(WS协议) + OneBotMessageParser(解析+notice事件分类) + OneBotActions(操作+base64图片编码) + OneBotConfig
│     │     ├── File/    FileAdapter（文件轮询，测试用）
│     │     ├── Http/    HttpBridgeAdapter(HTTP+WS混合协议，独立端口；POST /api/send 阻塞等突发统一返回 + /api/messages 聊天记录 + /ws 双向实时) + HttpBridgeConfig
│     │     └── 通用:    IAdapter / AdapterManager / AdapterFactory / AdapterStatus / AdapterAction / ActionResult / IEngineAwareAdapter / IChannelAwareAdapter / AdapterAccessImpl(IAdapterAccess桥接)
│     │     ├── 消息模型: IncomingMessage (含 IsSystemEvent/SystemEventSubType) + OutgoingMessage + MessageAttachment
│     ├── Client/      IModelClient 抽象层（Claude/OpenAI 双协议）+ Embedding + IVisionProvider + IOcrProvider
│     ├── Command/     框架指令系统（/help /status /config 等）
│     ├── Config/      PathConfig 绝对路径管理
│     ├── Core/        业务核心（AgentCore+ExtractionCore），继承 CoreBase，各自 JSON 配置
│     ├── Database/    实体 + Repository（SQLite，15张表：记忆 9 张 + 业务 6 张，另加日志库）
│     ├── Engine/      引擎生态（MasterEngine内核 + 7个ISubEngine + Components + Modules）
│     │     ├── Core/     MasterEngine内核 + Gate/Agent/ComponentHost等基类
│     │     ├── Session/  SessionEngine + RuntimeSession（会话管理+频道占用）
│     │     ├── Messaging/ MessageEngine（双向消息枢纽+去重分发）
│     │     ├── AdapterEng/ AdapterEngine（适配器生命周期+频道融合）
│     │     ├── Mind/     MindEngine（心智引擎：Writer+情绪+可信度仲裁+检索+伴随提取，常驻）
│     │     ├── Worker/   ExpressEngineSpawnCheck（渠道层调度：express 会话孵化）
│     │     ├── Timer/    TimerEngine（心跳30s）
│     │     ├── Vision/   VisionEngine（图片描述+OCR）
│     │     ├── Command/  CommandSpawnCheck（指令拦截）
│     │     └── Modules/  各模块（CompressionTierModule等）
│     ├── MCP/         MCP Client 桥接层（外部插件生态接入）
│     ├── Tool/        工具宿主（PluginLoader/ToolRegistry/ToolExecutor）+ 核心工具（continue_loop/wait）
│     ├── Util/        VectorUtil 向量操作
│     ├── WebUI/       Blazor Server 管理面板（嵌入式，同进程）
│     └── Program.cs   入口（WebApplication 宿主，默认启动 Web 服务器 + 适配器）
└── Plugins/                    源码项目（每个含 plugin.json + CopyToHostPlugins 目标）
      ├── Plugin.BasicTools/      speak + thinking + send_media + send_file + adapter_action
      ├── Plugin.WorkingTools/    thinking_notes + retain_list + task_management + mark_for_review + pin_* 系统记忆
      ├── Plugin.MindTools/       journal/claim/remember/mind_status/archive（心智工具）+ mind-emotion/mind-experience 注入组件
      ├── Plugin.FileTools/       read_text + write_text + update_text + list_dir + move_file/delete_file/copy_file
      ├── Plugin.FileOps/         archive + search/grep_files + file_info/hash/compare_files
      ├── Plugin.GroupFileTools/  分组文件操作
      ├── Plugin.SystemTools/     子 session 管理：create_sub_agent / stop_sub_agent / send_instruction / list_sub_agents / wait_sub / get_sub_info（system-ops 组件）+ report_to_parent（sub-agent-runtime 组件）
      ├── Plugin.ReviewTools/     8 个验证工具（review_list/resolve/preview/commit/evidence/declassify/dissolve/finish）
      ├── Plugin.ScheduledTasks/  定时任务（插件化实现）
      ├── Plugin.SkillTools/      invoke_skill / list_skills / load_skill_file / list_skill_files
      ├── Plugin.NetworkTools/    网络工具 + Playwright 浏览器自动化
      ├── Plugin.SshTools/        SSH 工具
      ├── Plugin.WebSearch/       网页搜索
      ├── Plugin.ImageTools/      图片处理
      ├── Plugin.SvgTools/        SVG 渲染
      ├── Plugin.DocumentTools/   文档处理
      ├── Plugin.Email/           邮件收发
      ├── Plugin.Currency/        货币工具
      ├── Plugin.CampusPrint/     校园打印
      ├── Plugin.GitTools/        Git/GitHub 仓库操作
      ├── Plugin.QuickActions/    快捷操作
      └── FileToolKit.Shared/     FileToolBase 抽象基类（路径解析/沙箱）

运行时输出结构（`{BaseDir}/Plugins/`）：
```
Plugins/
  Plugin.Email/                   ← 子目录 = 一个插件
    plugin.json                   ← 清单（id/entry/version/components 等）
    Plugin.Email.dll              ← 入口程序集
    Plugin.Email.deps.json        ← 依赖映射（AssemblyDependencyResolver）
    MailKit.dll                   ← NuGet 依赖
    ...
  Plugin.BasicTools/
    plugin.json
    Plugin.BasicTools.dll
    Plugin.BasicTools.deps.json
  ...
```
PluginLoader 扫描子目录 → 读 `plugin.json` → 加载 `entry` DLL。无需文件名白名单，依赖 DLL 不被单独扫描，消除竞态条件。
```

## 引擎生态

会话体系：基础设施引擎层（ISubEngine）+ 会话层（RuntimeSession，按需创建）。频道响应全面会话化（express 也是会话，无独立 ExpressEngine 引擎）。

### 上下文机制

**ChannelContextService** 为 express 与 working 等会话共用的上下文构建服务，统一格式化频道上下文。该服务输出 `ChannelContextState`，包含当前频道会话的完整上下文快照：
- **参与者信息**：从数据库派生（实时查询），无累积状态，反映当前频道活跃用户
- **消息历史**：按规则保留（最近 N 条+关键事件）
- **系统上下文**：频道配置+睡眠状态+模式标记

**时区统一约定**（2026-08-23 起，`TimeUtil`，存储全 UTC + 展示/调用转本地）：
- **存储全 UTC**：UserMessages 入库咽喉（`SessionManager.OnMessageAsync`/`SaveBotMessageAsync`/`RuntimeSession`）统一 `TimeUtil.NormalizeToUtc`（Local→UTC，Unspecified→按 UTC 解释）；scheduled_tasks.json 的 `CreatedAt`/`NextFireTime` 亦以 UTC（`Z` 后缀）落盘。存量混存数据接受现状不迁移。
- **展示转本地**：DB 读出 Kind=Unspecified（sqlite-net ticks），一律 `TimeUtil.ToLocalSafe`（按 UTC 解释再转本地，`TimeZoneInfo.Local` 动态跟随）；WebUI/HttpBridge/插件摘要均走此路径。
- **agent 调用参数**（如 `search_chat_history` 的 time_from/time_to）：输入语义=本地时间，`Unspecified` 显式锚定 Local，查询前 `NormalizeToUtc` 转 UTC。
- **系统时间注入**：`SessionAgentHost.BuildRoundAsync` 每轮注入 `[系统] 当前时间: {本地} {TimeZoneAnchorText}`（动态 offset，如 `UTC+08:00 (China Standard Time)`），替换旧硬编码 `UTC+8 (Asia/Shanghai)`。

**消息时间戳约定**（`ChannelContextService` 渲染，仅供模型上下文参考）：
- 每条消息带本地时区 `[HH:mm]`（`TimeUtil.ToLocalSafe(m.Time)`）
- **历史块**（`BuildHistoryBlockAsync`，主动拉取大块）：按天打戳——日期变化时输出独立日期行 `yyyy-M-d`，其后消息各带 `[HH:mm]`
- **增量块**（`FormatIncomingAsync`，session 自动推送）：每条带 `[HH:mm]`，**仅跨日**时前置一行日期戳（`ChannelContextState.LastRenderedLocalDate` 记录上次渲染本地日期，跨日判定）
- `Time` 为默认值（历史残留无时间数据）时跳过时间戳，避免 `0001-1-1` 污染

**Session 认知**构成：
- 频道端口集合：Session 挂载的每根端口对应一个频道，每频道贡献其 ChannelContextState（参与者+历史）
- 初始状态（InitialState）：Session 创建时的指令，排在所有频道上下文之后，驱动 Session 行为

这种设计避免参与者累积过期状态，同时支持 Session 跨多个频道关联信息。
会话层（IRuntimeSession，express/working/task 统一）:
  RuntimeSession — 统一 agent 循环，行为由 CreateSessionSpec 驱动（Type 决定 profile）
    express: 频道轻量对话（AutoClaim Full，权重触发，空闲回收）
    channel: 工作模式（escalate 后接管频道）
    task: 任务执行 / 子 agent

基础设施引擎层（ISubEngine，SpawnCheck 托管生命周期）:
  MasterEngine (内核，实现 ISystemContext)
    ├── SpawnCheck 注册表 (IEngineSpawnCheck)
    │     Timer      → TimerEngine (心跳30s，IsInfrastructure=true)
    │     Message    → MessageEngine (双向消息枢纽，IsInfrastructure=true)
    │     Adapter    → AdapterEngine (适配器管理+频道融合，IsInfrastructure=true)
    │     Session    → SessionEngine (会话生命周期+资源池，IsInfrastructure=true)
    │     Mind       → MindEngine (心智引擎：唯一写入口 Writer+情绪+可信度仲裁+检索，常驻 AutoStart)
    │     Vision     → VisionEngine (图片描述+OCR，IsInfrastructure=true)
    │     Command    → CommandSpawnCheck (指令拦截)
    │     Channel    → ExpressEngineSpawnCheck (无占用消息→孵化 express 会话；同时喂伴随提取攒批)
    ├── 活跃引擎表 (List<ISubEngine>)
    ├── GlobalComponentHost + ComponentHost(per-session) + ModuleBus(per-session)
    │     Component 系统: IGlobalComponent(全局) / ILoopComponent(per-session)（接口定义在 AgentLilara.PluginSDK/）
    │     ComponentRegistry: 类型注册，PluginLoader 扫描 [Component] 标记
    │     ComponentHost: 实例管理 + 工具注册/反注册 ToolRegistry + ModuleBus 订阅
    │     ModuleBus: 每引擎独立 pub/sub（替代旧 ILoopBus + ComponentEventBus）
    └── 事件流水线: EventBus → HandleEventAsync
          ① 内核更新 (lastMessageTime)
          ② SpawnCheck: OnEventAsync → ShouldSpawnAsync → Create → StartEngine
          ③ 派发 OnEvent 给活跃实例
          ④ 清理 IsAlive=false 的实例

会话层（IRuntimeSession，不定生命周期）:
  RuntimeSession — 统一 agent 循环，行为由 CreateSessionSpec 驱动
    场景: Working（频道全功能）/ Task（任务执行，禁止套娃）/ Eavesdrop（监听判定）
    端口: IInputPort（注入上下文，按权重激活）/ IOutputPort（工具写端口，决定工具注册）
    创建: SessionEngine.CreateSession(CreateSessionSpec) — 组件列表+端口配置+初始状态
```

ISubEngine: EngineType / RunAsync / OnEvent / IsAlive / RequestStop / IsInfrastructure(默认false)
ISystemContext: 数据访问 + 适配器 + EventBus + 引擎查询 + StartEngine/RequestStopEngine + CurrentSleepState + MuteMode + GlobalComponentHost + ComponentServices

### 引擎内部循环架构（Session 统一模型）

```
RuntimeSession 内部（取代旧 Channel/System 引擎内循环）:
  Gate (闸门)
    ├── delegate 驱动，组合不继承
    ├── WaitAsync(timeout) → 放行后立即重置
    └── Signal() 升闸（端口权重/超时触发）

  Agent (多轮推理循环)
    ├── 构建上下文 → 调模型 → 执行工具 → 是否继续
    ├── 退避策略: 连续失败 → exponential backoff
    ├── OnToolExecuted 回调 → 宿主发布事件到总线
    ├── ConversationOffset: 区分框架注入和对话内容
    └── StopReason: Completed / MaxRounds / WaitRequested / ForceStopped / Error

  IAgentHost (宿主接口，Session/RuntimeSession 实现)
    ├── BuildStartInjectAsync(): 每次唤醒注入一次（固定前缀/摘要/记忆/新消息/组件目录）
    └── BuildRoundInjectAsync(): 每轮注入（实时状态/工具结果/信号缓冲）

  IInjectProvider (插件/组件注入接口)
    ├── BuildStartInjectAsync(InjectContext): 稳定快照，Start 时机
    ├── BuildRoundInjectAsync(InjectContext): 实时数据，每轮
    └── InjectPriority: 排序

  ModuleBus (每 Session 独立 pub/sub)
    ├── Subscribe<T>(Action<T>): 订阅事件
    ├── Publish<T>(T): 发布事件
    └── 事件类型: ToolExecutedEvent / RoundEndingEvent / SpeakRequestedEvent / MemoryStoreEvent / SignalEmitEvent

  Session 持久化（三件套，重启续跑 + 死的不加载）
    ├── Storage/Sessions/{sessionId}/spec.json    — 创建时一次写：组件/端口/InitActions/InitialState/Limits
    ├── Storage/Sessions/{sessionId}/state.json   — 每轮原子重写：status/roundCount/WaitUntil/连接/channelCursors/activeCoreConfig
    └── Storage/Sessions/{sessionId}/history.jsonl — 每轮追加：assistant + tool_result 一起写
    恢复时仅加载 status ∈ {Running, Crashed} 的存档；Completed 留档不加载
```

## 消息处理流

```
入向:
适配器 ──► AdapterEngine ──► MessageEngine (入库+去重+标记+分发)
                                  │
                                  ├── express/working 会话 (端口权重激活，频道 Full-claim 时)
                                  └── ExpressEngineSpawnCheck (无占用→孵化 express 会话
                                        + 喂 MindEngine 伴随提取攒批)

出向:
Session ── speak/send_media ──► MessageEngine (出向入库) ──► AdapterEngine (发送) ──► 适配器

AdapterEngine: 托管适配器生命周期 + 频道融合(多适配器合并一条流) + 消息分段(按平台规则拆分)
  发送决策: 选 Priority 最高、AdapterId 最小的活着适配器

MessageEngine: 双向枢纽
  入向: AcceptIncoming → DB 去重(ChannelId+MessageId) → 标记 tag(normal/mention/command) → 推订阅者
  出向: SendOutgoing → INSERT(SentByBot=true) → 发 AdapterEngine → 适配器实际发送
  子流: 每条频道按 tag 拆 normal / mention / command
  订阅: Subscribe(channelId, subStream) → IInputPort，按权重(mention=100/normal=5)激活 Session

频道层调度（ExpressEngineSpawnCheck + express 会话）:
  定位: 频道响应已全面会话化。无占用频道收到消息 → ExpressEngineSpawnCheck 孵化 Type="express" 会话
    （AutoClaim Full 接管频道），express 会话按端口权重激活响应。不再有 ExpressEngine 引擎/冲动值。

  入向路由（ExpressEngineSpawnCheck.HandleMessageAsync）:
    消息入库+身份映射 → 频道被会话 Full-claim？→ DeliverToSession 投会话端口
    → 无占用 → CreateExpressSessionForChannel 孵化 express 会话（AutoClaim Full）→ 投递
    私聊/提及 → 投 mention 端口（Weight=100 立即激活）；普通消息 → normal 端口（Weight=5 攒满 100）

  express 会话（Type="express"，RuntimeSession）:
    AutoClaim Full → 顶层资源池持有 claim。express 与后续 escalate 的 working **共享同一资源池**，
    escalate 时同池换活跃 session（claim 原地不动，不释放不重取）。
    被动响应: awaitingInput=true 强制，按端口权重激活，不因 InitialState 立即推理。
    空闲回收: SessionEngine 30s 循环扫描 express 会话空闲超时（ImpulseConfig.ColdTimeoutSeconds）→
      直接杀 + 释放 claim（频道回无占用）。express 记忆只读+历史突发语义，无长期状态可压缩。
    工具白名单（Storage/SessionTypes/express.json 的 Tools，白名单模式）:
      speak/thinking/send_media/send_file/adapter_action/escalate/wait/connect_channel/disconnect_channel
      + 会话工具(list_channels/search_chat_history/list_sessions/send_session_message)
      + 心智只读(journal/remember/mind_status)
    身份提示词: SessionTypeProfile.PromptFiles（express_workflow.txt），InitialState 只放实例特有内容。

  escalate 行为（同池换活跃 session）:
    express 会话调 escalate → SessionEngine.CreateFromEscalateAsync
    → 同池 TakeOver 到新 working id（claim 原地不动）→ CreateSession("channel", fixedSessionId)
    → working 接管频道，express 结束（ParentSessionId 记录父子关系）。

SessionEngine (ISubEngine, IsInfrastructure=true):
  30s 监控循环:
    检查所有 WaitUntil <= now 的 Session → 通过输入端口注入 timeout 消息激活
    ScheduledTask 检测 (ExecuteAt <= now) → CreateSession("task")
    TODO 扫描 (pending, MinBalance 满足) → 创建 Session 或注入相关频道

  创建: IRuntimeSession CreateSession(CreateSessionSpec spec)
  停止: StopResult StopSession(id, reason) — 子未停阻塞，ForceKill 可强杀整个树
  占用: ClaimChannel(sessionId, channelId, ClaimLevel.Full/Listen) — 占用状态在资源池（SessionPool）
        Full → 接管频道（消息投会话端口，无独立 express 引擎）
        Listen → 只读消息流
        同一频道允许多个 Listen，最多一个 Full
  自动占用: express/working 会话创建时自动 Full 占用归属频道（AutoClaim）

  崩溃恢复:
    Storage/Sessions/{sessionId}/*.json → 扫描 → Running/Crashed → 恢复 → 继续运行

伴随提取 (MindEngine.Extract.Express，无独立引擎):
  入向咽喉 ExpressEngineSpawnCheck.HandleMessageAsync 把消息喂进 ExpressExtractor 攒批
  （fire-and-forget，达阈值时的 LLM 调用不阻塞消息链路）。

  触发: 攒够 20 条自动跑 | SessionEngine 30s 循环空闲补一次（>=3 条且静默 120s）
        | 优雅退出/--test 收尾 FlushAllAsync 清尾巴 | WebUI 频道页手动触发
  调用: ExtractionCore（Core/，继承 CoreBase，单轮无工具）
        指令模板 Storage/Core/Prompts/ExtractionPrompt.txt（可热改，不用重编译）
  产出: 语义三元组断言（subject/predicate/object 完整槽位），非"用户说了X"式 literal
        可信度由模型按上下文态度自评，缺省 0.7（默认不假设为真），夹取 [0.05, 0.95]
        同一消息的多条断言共享 utterance_id（防重复计分）
  容错: 解析失败/槽位残缺 → 丢弃该条，不兜底造断言（消息仍在库里可重提）
  范围: 仅聊天文本消息（bot 发言作上下文不作提取对象）；工具调用内容归善后复活

  旧 ExtractionEngine / ChannelExtractionWorker / EavesdropTask 已于 2026-08-11 删除
  （提取核心并入心智后它们只剩进度标记，从未真正提取）。

RuntimeSession (统一 agent 循环, 无强制 stop):
  不设 MaxLifetime/IdleTimeout 上限——仅由模型 end_session / 外部 RequestStop / CTS 取消终止。
  wakeLoop: 由 SessionEngine.CreateSession 传入 `() => session.WakeUp()`，
    组件/GlobalComponent 通过 MasterEngine.WakeLoop → SessionEngine.WakeUpSession
    → RuntimeSession.WakeUp（设 volatile bool _wakeSignaled）唤醒等待中的 session。
    WaitForActivationAsync 每轮检测 _wakeSignaled，检测到后清标志并返回 true。
  组件通知排空：每轮 agent 推理前调用 _componentHost.OnBeforeInvokeAsync() 排空
    异步任务完成通知（下载/SSH/群文件等），再调用 BuildPromptSections() 将其
    注入 context（以"[组件通知]"消息呈现给模型）。

  不分子类，差异由 CreateSessionSpec 驱动:
    ComponentIds: 启用的组件列表
    MaxLevel: 工具等级上限 (0=L0全开, 1-3=预留)
    InputPorts: 端口名 → (频道, 子流, 权重)
    OutputPorts: 端口名 → 目标
    AutoClaimChannel: 自动 Full 占用归属频道

  默认配置（SessionTypeProfiles 内置种子，Storage/SessionTypes/{type}.json 可覆盖）:
    channel: basic-tools, working-tools, file-tools, file-ops, mind-tools, system-ops | AutoClaim Full
    task:    basic-tools, file-tools, mind-tools, sub-agent-runtime | AutoClaim=false
    express: basic-tools | AutoClaim Full（孵化时接管频道）

  激活:
    端口权重达阈值(mention=100 立即/normal=5 攒多条) → 激活
    WaitUntil 到期 → SessionEngine 30s 循环注入 timeout 消息 → 激活
    创建后直接启动（无输入端口的 TaskSession）

  权限:
    工具等级 L0-L3（目前几乎全 L0）
    Session.MaxLevel 过滤
    enable_component / disable_component：Session 自行管理组件，L0 行为

  持久化:
    持久化三件套已在 SessionEngine + RuntimeSession + SessionPersistence 完成
  spec.json (组件/端口/InitActions/InitialState/Limits) + state.json (status/roundCount/连接/游标) + history.jsonl (每轮追加 assistant + tool_result)

  轮次定价（对接 ICurrencyService）:
    前 20 轮免费，之后 ceil((N-20)/10) 币/轮
    余额不足 → 注入警告 → Session 自行收尾
    余额分级: L1 提醒(≤500) / L2 收紧(≤200) / L3 危急(≤50) → 能力收紧+全局警告

压缩模块 (CompressionTierModule + CompactTool，接入 RuntimeSession):
  CompressionTierModule: 三层判定（Evaluate + GetInjectText）
    阈值: L1=80k / L2=130k / L3=150k（基于 200k 上下文模型）
    L1 温和提醒 / L2 明确催促 / L3 触发 onL3Triggered 回调
  CompactTool: 模型调 compact(summary) 触发新 session 接管旧 session
    旧 session EndSession 退出循环，新 session 走标准 CreateSession 路径
    ParentSessionId = 旧 sessionId，InitialState = "[上一会话摘要]\n{summary}\n\n请继续工作"
    L3 锁定工具表只剩 compact（_preLockdownTools 备份），模型必调
  ChannelContextPersistence: per-channel JSON 原子写入（游标接力，与 session 持久化解耦）
```

### Session-scoped 子资源 (ISessionResource)

子 agent / 下载 / SSH 任务都实装 ISessionResource，注册到 SessionEngine。
SessionEngine 持有 _sessionResources 注册表（任务键为裸 sessionId）：
- compact 时通知所有 ISessionResource.OnSessionCompacting(oldId, newId)，资源改自己的 sessionId（compact 换根由资源池统一处理，无手动 rebind）
- session 死时通知 OnSessionFreed，资源清理状态
- 外部唤醒通过 ResolveActiveSession(sessionId) 经资源池树解出活跃 session（引用 → 所在池 → 池活跃 session）
- end_session 通过 ILoopComponent.OnCanExitAsync + SessionEngine.AnyResourceBlocksExit 阻塞有 in-flight 资源的退出

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

## 做梦系统

```
旧 DreamEngine / ReviewEngine（旧复盘引擎）已随记忆重构整体移除，三阶段架构、关系分类、
ChangePropagation、临时记忆爆仓触发等旧机制全部删除。

维护与探索（尚未实装）:
  按 docs/memory-redesign/03 规划走 MaintenanceEngine + Dream 探索者（切片式轻量探索，
  无 session，产出交 Writer / ReviewQueue），调度节奏见 docs/memory-redesign/06 待定清单。

睡眠分级（保留）:
  SleepState 枚举 + 分级打断语义（走神被@醒 / 小睡需@+关键词叫醒、仅@触发梦话 / 大睡仅 force-wake 可唤醒）。
  force-sleep 信号仍发布，但暂无消费引擎（旧 DreamEngine 已删）。

梦话 (SleepTalkCore): 已随死代码清理删除（2026-08-24，DreamEngine 已删、类零实例化）

Review 验证（替代旧 ReviewEngine 自由探索复盘）:
  完整流程见 docs/memory-redesign/05：ReviewQueue 唯一工作源（Conflict/Dream/Manual 入队）
  + review_* 工具链（review_list/resolve/preview/commit/evidence/declassify/dissolve/finish）
  + git 式暂存提交（ReviewStage：三类操作——可信度调值 / 解除排他 / 拆解冲突组，
    review_preview 预览影响 → review_commit 原子事务落库）。
  消费端（2026-08-13 接通）: SessionEngine 30s 循环按需孵化 review 会话
    （队列有活 + 无活跃 review 会话），per-session 注册 IReviewStageAccess
    （CreateSessionSpec.SessionServices → ComponentHost perLoopServices）。
  排他自动标记: Writer 写 attribute 谓词首建时 IsExclusive=true（默认排他），
    解除后 sticky（GetOrCreateAsync 尊重已有记录，不再升回）。
```

## 记忆系统

```
心智引擎（MindEngine）承载记忆模型，架构见 docs/memory-redesign/（01 存储模型 / 03 心智引擎 / 05 可信度冲突模型）。

存储（docs/memory-redesign/01）:
  Claims       — 断言三元组（subject/predicate/object 槽位 JSON），带 Credibility（可信度锚点）/
                  strength（程度值 -1~+1）/ IsInference（推断标记），否定 = 支持 + 负 strength
  Journals     — 经历正文（L2 Experience / L3 Reflection），第一人称不可变，携带 EmotionSignals
  Keywords     — 关键词库（attribute/keyword 角色 + IsExclusive 排他标记），经 FragmentKeywords 关联 Claims/Journals

唯一写入口 Writer（Engine/Mind/Writer.cs，docs/memory-redesign/03）:
  WriteBatch 原子批次（claims + journals 一次事务）：关键词关联 → 同源分组（utterance_id）→
  冲突检测 → 情绪应用 → 证据重算 → 落库；EmotionState.json 事务成功后原子写

证据引擎 EvidenceEngine（docs/memory-redesign/05）:
  支持/反对边（断言引用断言，反对 = 支持 + 负 strength）→ noisy-OR 组内聚合（质量>数量）+
  logit/sigmoid 融合算实际可信度 c_actual（锚点 + 证据网络派生，用于召回/展示/触发 Review）

冲突检测 ConflictService（docs/memory-redesign/05）:
  排他属性槽位（Keywords IsExclusive=true）取值不同 → 建组/入组；组内成员 c_actual 和 ≤1 恒定，
  超预算（和 >1）→ 触发 ReviewQueue（Source=Conflict）

Review 验证（docs/memory-redesign/05）:
  ReviewQueueService 待验证队列（唯一工作源：Conflict/Dream/Manual 入队，崩溃恢复）
  + ReviewStage git 式暂存提交（review_preview 预览影响 → review_commit 原子事务落库）
  review_* 工具链见 Plugin.ReviewTools

检索 RetrieveService（docs/memory-redesign/01）:
  关键词 + 时间窗口；排序两级：命中关键词数降序（绝对优先级）→ c_actual 降序；返回二跳内子证据；
  同 utterance_id 的多条断言并排返回

情绪 EmotionService（docs/memory-redesign/02）:
  五轴（兴奋/喜悦/敏觉/满足/积极，-1~+1）+ 运动公式实时应用 + EmotionState.json 原子写 + EmotionSnapshot 快照表

框架事件 EventSink（docs/memory-redesign/03）:
  工具失败/超时/用户否定等 → L0/L1 落 FrameworkEvents 表（不污染 Claims/Journals，可作 Evidence 引用）
```

数据库表（docs/memory-redesign/01 已实施）:
  记忆表（9 张）: Claims / Journals / Keywords / FragmentKeywords / EmotionSnapshots / FrameworkEvents / ConflictGroups / ConflictGroupMembers / ReviewQueueItems
  业务表: Person / User / Channel / UserMessage / Image / ModelCallLog 等
  旧表（Memories/TempMemories/MemoryLinks/PersonaMemories/Beacons/PersonTraits/DreamLogs/ReviewLogs/EvaluationScores 等 12 张）已 DROP
日志库(logs.db): events / token_usage

## 工具系统（插件化架构）

```
AgentLilara.PluginSDK (共享契约，独立类库):
  ITool: Name / Description / Parameters / Timeout / ExecuteAsync / GetInputSchema()
  IToolContext: GetService<T>() / Storage
  IPluginStorage: GlobalDirectory / InstanceDirectory
  IPromptContributor: SectionKey / Priority / BuildSection()
  ToolMetaAttribute: Group / ContinueLoop / OutputOnly
  ToolParameter: Name / Description / Index / IsRequired（控制 JSON Schema required 数组）
  Services/: IWriterAccess（记忆写：WriteBatch 原子批次）/ IRetrieveAccess（关键词+时间窗口检索）
             IEmotionAccess（情绪读取/信号提交）/ IReviewQueueAccess（待验证队列）
             IReviewStageAccess（git 式暂存提交）
             IChannelAccess / ISubAgentAccess / IAdapterAccess / IEngineAccess
             IEventBusAccess / IToolHistoryAccess
             IConnectedChannel（会话频道状态）/ ICurrencyService（货币）/ IImageAccess（图片+OCR）
             ISpeakGuard（防话唠）/ ISessionResourceAccess（会话资源）/ IProductRegistry（商品注册）

主程序 Tool/ (宿主层):
  Core/CoreTools.cs          — 核心工具（continue_loop + wait），不可卸载
  Core/EscalateTool.cs       — Express escalate 工具，触发 SessionEngine.CreateSession
  Host/PluginLoader          — 扫描 {BaseDirectory}/Plugins/*.dll，AssemblyLoadContext 隔离加载
  Host/ToolContextImpl       — IToolContext 实现（ConcurrentDictionary 服务容器）
  Host/WriterAccessImpl      — IWriterAccess 桥接（Writer 写入口）
  Host/RetrieveAccessImpl    — IRetrieveAccess 桥接（关键词+时间窗口检索）
  Host/EmotionAccessImpl     — IEmotionAccess 桥接（情绪读取/提交）
  Host/ReviewQueueAccessImpl — IReviewQueueAccess 桥接（待验证队列）
  Host/ReviewStageAccessImpl — IReviewStageAccess 桥接（暂存提交）
  ToolRegistry               — 全局注册表（Register/Unregister/Get/禁用管理）
  ToolExecutor               — 顺序执行 + 会话类型硬校验 + OnToolExecuted 回调
  SessionTypeProfiles        — 会话类型档案（**唯一权限源**，Storage/SessionTypes/{type}.json：Core/AllowedCores/Components/Tools 白黑名单/Prompts 自包含；内置 express/channel/task/review 种子；任意新类型=新 JSON）

  工具可用性（会话类型文件 = 单一裁决源）:
    1. ToolConfig.json 全局禁用（kill switch，独立概念）
    2. SessionTypeProfiles 会话类型白/黑名单（Storage/SessionTypes/{type}.json 的 Tools：whitelist=仅清单可用 / blacklist=清单禁用）
    3. 组件装配 = 同文件的 Components 清单（Loop 组件按清单装载；Global 组件恒装载、其工具由工具清单门控）
  会话类型名即权限作用域（无 PermissionGroup 间接层）。AgentCore.EngineType = 会话类型 key。
  核心过滤点：ToolRegistry.IsVisible(name, type) → SessionTypeProfiles.IsAllowed；
  执行期：ToolExecutor.PermissionGroup 对类型外工具硬拒绝。
  旧 PermissionService / ComponentConfig 已删除。

插件项目 (独立 DLL，输出到 {BaseDirectory}/Plugins/):
  Plugin.BasicTools      — speak + thinking + send_media + send_file + adapter_action（基础通信）[LoopComponent: basic-tools]
  Plugin.WorkingTools    — thinking_notes + retain_list + task_management + mark_for_review（工作状态+复盘标记；mark_for_review 走 IReviewQueueAccess 入 ReviewQueue，Source=Manual）[LoopComponent: working-tools] + Global 组件 pinboard（文件式系统记忆：Storage/Workspace/pins/*.md，会话开始时注入仅标题清单，pin_* 工具家族，旧 pinboard.json 迁移）
  Plugin.MindTools       — journal/claim/remember/mind_status/archive（心智工具）+ mind-emotion/mind-experience 注入组件 [GlobalComponent: mind-tools]
  Plugin.FileTools       — read_text + write_text + update_text + list_dir + move_file/delete_file/copy_file（文件系统）[GlobalComponent: file-tools]
  Plugin.FileOps         — archive_create/extract/list + search/grep_files + file_info/hash/compare_files
  Plugin.GroupFileTools  — 分组文件操作
  Plugin.SystemTools     — 子 session 管理（system-ops 组件：create_sub_agent/stop_sub_agent/send_instruction/list_sub_agents/wait_sub/get_sub_info；sub-agent-runtime 组件：report_to_parent）
                          SubAgentAccessAdapter 桥接 SessionEngine（无 CrossRequest 镜像，纯消息驱动）：
                            - 创建子 session 时附加 MailboxInputPort（父→子指令通道）
                            - 子 session OnCompleted 钩子 → 直接 InjectExternal 通知父 session + 触发 wait_sub
                            - report_to_parent 工具直接 InjectExternal 注入父 session 上下文（单条路径，无双轨）
  Plugin.ReviewTools     — review_list/resolve/preview/commit/evidence（5 个验证工具链）[LoopComponent: review-tools]
  Plugin.ScheduledTasks  — 定时任务（插件化实现）
  Plugin.SkillTools      — invoke_skill / list_skills / load_skill_file / list_skill_files
  Plugin.NetworkTools    — http_request + download_file [LoopComponent: network-tools] + Playwright 浏览器自动化 10 工具 [GlobalComponent: browser + LoopComponent: browser-tools]
  Plugin.SshTools        — SSH 工具
  Plugin.WebSearch       — 网页搜索
  Plugin.ImageTools      — 图片处理
  Plugin.DocumentTools   — 文档处理
  Plugin.QuickActions    — 快捷操作

ToolCall: 原生 tool_use (Claude API) 为主路径
  NativeToolCallHandler: 按 properties 顺序映射命名参数到位置输入
  参数名必须英文（Bedrock 代理不支持非 ASCII schema 属性名）

循环控制:
  RuntimeSession (express/working/task): 端口权重驱动激活（mention=100 立即 / normal 攒满 100 / wait 超时）
    express 强制 awaitingInput（被动响应）；working 主动工作（end_session 终止）
    express 空闲回收（ColdTimeoutSeconds）→ 直接杀 + 释放 claim
    无强制上限：仅由模型 end_session / 外部 RequestStop / CTS 取消终止

插件加载:
  目录: {程序目录}/Plugins/（跟程序走，不跟 Storage 走）
  结构: 每个插件独立子目录，内含 plugin.json 清单（id/entry/version/components）
  扫描: PluginLoader 扫子目录 → 读 plugin.json → 加载 entry DLL
  隔离: 每个入口 DLL 用独立 AssemblyLoadContext（支持卸载）
  依赖解析: deps.json（AssemblyDependencyResolver）+ 目录回退（插件目录下直接探测）
  安全: 依赖 DLL 不在独立 ALC 中加载，消除竞态条件
  多类型发现: ITool / IInjectProvider / IWebUIProvider / ILoopComponent / IGlobalComponent
  构造注入: EventBus / ModuleBus / Gate / IWriterAccess / IRetrieveAccess / IEmotionAccess / IServiceProvider
  延迟实例化: IInjectProvider/Component 由引擎创建（非全局单例），ITool 是全局单例
  启动时 MasterEngine.InitAsync 调用 PluginLoader.LoadAll()
  服务注入: ToolContext.Register<IWriterAccess>/<IRetrieveAccess>/<IEmotionAccess> 等心智服务在插件加载前完成
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
  MCP 工具与内置工具共享 GenerateDescriptions
```

## 用户系统

```
Person (自然人)          User (账号)
  Id                       Id
  TrustLevel (-2~5)        PersonId → Person
  TrustProgress (好感度)   Platform + PlatformId
  FastMemory (一句话概括)  PermissionLevel (Blocked~Admin)
  DisplayName

信任系统已暂缓（见 docs/memory-redesign/06）:
  旧信任升降级/好感度/警报惩罚流程不再运转——TrustProgressionConfig 保留但旧信任字段
  迁移到"信任 = 评估断言"未做；AlertLevel 已取消。
  目标形态（docs/memory-redesign/03 人物与账号）: 信任是 Lilara 对某人物的一条评估断言
  （{subject:Lilara, predicate:信任, object:人物, object_value:等级, Credibility:可信度}），
  日常事件不更新，仅 Review 审视该人物时重估等级 + 可信度（评估断言）——未实装。
  TrustLevel 语义沿用: Hostile(-2) / Wary(-1) / Unknown(0) / Stranger(1) /
            Understanding(2) / Familiarity(3) / Trust(4) / AbsoluteTrust(5)
  冷启动默认 Unknown，首次 Review 审视该人物时才建信任断言

一个 Person 可关联多个 User（跨平台账号）
Console 平台用户默认 Admin 权限
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
├── Core/*.json          各Core的LLM配置（连接+采样+协议，不含 session 提示词）
├── Core/Prompts/        核心提示词（ExtractionPrompt.txt 经代码 PromptLoader 加载；System/SleepTalk 提示词已随死核清理删除）
├── Core/VisionProvider.json  视觉模型配置(apiKey/endpoint/model)
├── Core/OcrProvider.json     OCR模型配置(apiKey/endpoint/model)
├── Engine/
│     ├── EngineConfig.json          自启动引擎列表 {"autoStart":["Message","Timer","Session","Vision","Mind"]}
│     ├── ImpulseConfig.json         仅 ColdTimeoutSeconds 被读取用于 express 空闲回收（其余冲动字段为死配置，不参与决策）
│     ├── TrustProgressionConfig.json 信任升降阈值配置（信任系统暂缓，见 docs/memory-redesign/06）
│     ├── VisionEngineConfig.json    视觉引擎并发/批量/重试配置
│     └── Emotion/
│           └── EmotionState.json    情绪五轴当前值（-1~+1，原子写，重启加载，见 docs/memory-redesign/02）
├── SessionTypes/（会话类型配置库：类型键=文件名，字符串驱动，`SessionTypeProfiles.All()` 可枚举）
│     ├── channel.json               会话类型 channel（核心/组件/权限组/AllowedCores 白名单/PromptFiles）+ 工具黑白名单
│     ├── task.json                  会话类型 task（核心/组件/权限组/AllowedCores 白名单/PromptFiles）+ 工具黑白名单
│     ├── express.json               会话类型 express（轻量对话，AutoClaim Full + 白名单工具 + express_workflow 提示词）
│     ├── review.json                Review 验证会话（复用 TaskCore，组件 review-tools/mind-tools，PromptFiles ReviewPrompt+mind_usage）
│     └── Prompts/                   session 身份提示词主库（会话身份，SessionTypeProfile.PromptFiles 引用；换核不换提示词；含 mind_usage.txt 心智工具用法）
├── Adapter/
│     └── *.json  适配器实例配置（多实例，config-driven）
│         qq-main.json: OneBotAdapter 配置(WS地址/token/白名单/botNames)
│         http.json: HttpBridgeAdapter 配置(port/token/defaultChannel/channelPrefix/burstTimeoutMs)
├── Command/
│     └── CommandConfig.json  指令前缀配置
├── MCP/
│     └── McpServers.json     MCP Server 声明配置(传输/工具组/权限/覆盖)
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
  SystemMonitor — 触发告警采集（CollectAlerts + OnStateChanged 通知）
  快照: 各 Provider 直接拉取（EnginesProvider 用 MasterEngine.GetActiveEnginesSnapshot()、VisionEngine.GetSnapshot() 等）
       MasterEngine.GetSpawnCheck<T>() / GetActiveEnginesSnapshot()

页面 (卡片系统 Phase 3 已完成；仍保留少量独立页面 Dashboard/LogTrace/Login/McpStatus):
  Dashboard    — 系统状态/引擎摘要/活跃频道表格/实时日志尾部
  Logs/Trace   — Signal 信号追踪（SVG渲染/虚拟化/实时推送/因果链高亮）
  Logs/Tokens  — Token 统计（按 Core / 按 Model 汇总）
  Logs/Model   — 模型调用历史 + 详情查看
  Console      — 频道选择/创建 + 聊天式消息流 + 模拟用户/替Bot说话/自定义发送者 + @提及/私聊模拟
  McpStatus    — MCP Server 状态
  其余页面通过卡片系统动态渲染（/p/{route}）

Provider 数据源（WebUI/Providers/，共 17 个）:
  Memory — Claims/Journals/Keywords/情绪快照
  Review — ReviewQueue/冲突组
  Dream  — 框架事件 + 推断断言

启动行为:
  默认启动 Web 服务器（所有模式），--debug / --test 模式不启动 Web 服务器
  ASP.NET 日志压制到 Warning 级别，控制台输出干净

卡片式数据驱动系统 (Phase 1+2 已实现):
  SDK 接口: IWebUIProvider / IDataSource / CardSchema(9种) / IPageContext / DataQuery
  Shell: ProviderRegistry + DynamicPage + CardGrid + CardHost + 9种卡片渲染器
  路由: /p/{route} → ProviderRegistry.FindPage → DynamicPage 渲染
  数据层: DataSourceManager (Fetch重试3次指数退避 + Subscribe推送)
  Provider 发现: PluginLoader 扫描 IWebUIProvider 实现类 (与 Component/ITool 同管道)
  热重载: ALC 卸载 → 反注册 → 重新加载 → 注册 → 侧边栏自动刷新
  卡片类型: Table / Status / Form / Stream / Chat / Tree / Detail / Action / PropertyGrid (+ Custom 预留)
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
  ├── TokenAggregator (Model close → token_usage 聚合；detail 键名 caller/cached_tokens)
  ├── SignalConfig (Storage/Logs/signalconfig.json，MinLevel 运行时开关，Debug=0 排查用)
  └── LogQuery / LogAccessImpl (查询 + SDK ILogAccess 桥接)

SDK 接口: ISignalLogger (写) / ILogAccess (读写，继承 ISignalLogger)

信号模型:
  Signal.Begin → 新信号源◉（适配器收消息、Timer每tick独立信号）
  Signal.Continue → 继承上游信号（引擎生命周期/会话，带cause_span_id斜线；signalId 一律 NewSignalId 不与上游合并）
  Signal.Open/Close → 操作跨度（轮次/模型调用/每个工具独立span/记忆检索）
  Signal.Event/Error → 时间点日志/异常
  Close节点自动携带open的name（[完成] 处理轮次）
  SignalContext.Restore → Close后恢复父context，防止后续Open挂到已关闭span下
  每工具 span：ToolExecutor.RunSingleAsync 为每个工具开 LogGroup.Tool span（输入→status/data/error）

WebUI 日志追踪页 (/logs/trace):
  独立实现（不走卡片系统），LogTraceProvider 仅注册导航
  SVG 渲染引擎: slot分配 + 竖线/节点/斜线统一SVG
  虚拟化: 只渲染可视区±30行，rAF节流
  实时推送: 订阅 LogWriter 事件流
  交互: 悬停高亮+详情预览 / 单击锁定详情 / 双击筛选同信号 / 右键解锁
  文本区: 时间 / 来源(scope列) / 事件名 + 详情标签
  Close节点自动带open名称([完成] 处理轮次)
  列头中文显示(频道123/Session/Timer心跳等)
  悬停暂停自动滚动，滚轮恢复
```
