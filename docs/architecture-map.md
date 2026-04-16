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
├── Core/        业务核心（11个），继承 CoreBase，各自 JSON 配置
├── Database/    实体 + Repository（SQLite，11张表）
├── Engine/      引擎生态（MasterEngine 内核 + 子引擎）
├── Memory/      MemoryService 检索管线
├── Tool/        工具接口 + DAG 执行器 + 全局/局部工具集
├── Util/        VectorUtil 向量操作
└── Program.cs   入口（--qq / --file / --test / --mute / 默认 Console）
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
  消息缓冲聚合 (2.5s窗口) → 冲动值决策 → ProcessBatch:
  ① 构建 XML 上下文 (<participants> + <history> + <new>)
  ② PreprocessingCore Embedding 二分类 (聊天/任务)
  ③ MemoryService.RecallAsync 检索记忆
  ④ 路由:
     聊天 → ExpressCore (分条输出，逐条发送+随机延迟)
     任务 → WorkingCore Agent 循环 (多轮工具调用，子agent支持)
  ⑤ MemoryExtractionCore 异步提取记忆 (每3条触发)
```

## 冲动值决策 (per Channel)

```
私聊/控制台 → 缓冲聚合后一定回复
@提及(CQ at 或文本包含 botNames) → 穿透冷却期，必回
群聊 → 冲动值 ≥ 有效阈值:
  累加: BaseMessageScore(1.0) × channelAffinity × participantFactor
    participantFactor: 1人=1.0, 2人=0.9, 3人=0.8, 4+=0.6
  衰减: 每秒 -0.5
  有效阈值: 3.0 + consecutiveIgnores × 1.5 (最多3次)
  发言后冷却 3s
  负反馈: 仅主动发言(非@/私聊触发)后才等待回应
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

11张表: Person / User / Channel / UserMessage / MemoryEntry / TempMemoryEntry / MemoryLink / PersonaMemoryEntry / ReviewHint / (Topics 保留但不再使用)

## 工具系统

```
ITool: Name / Description / Parameters / Timeout / ExecuteAsync / AllowSubAgent(默认true)
ToolCall(JSON): tool + toolId + inputs(value/ref) + output + outputToModel + retain
ToolExecutor: DAG拓扑排序 + 分波并行 + 寄存器(跨轮) + 可选toolResolver

全局工具 (ToolRegistry, WorkerEngine用):
  文件流读取器 / 文件流写入器 / 说话 / 完成 / 思考笔记 / 记忆
  睡眠许可 / 强制睡觉 / 修改睡眠配置 / 调整睡意 / 触发红色警报 / 标记复盘
  委派任务 / 查看子agent / 任务管理

Review专用工具 (ReviewEngine内部):
  检索记忆 / 查看关联 / 读取消息历史 / 写入临时记忆 / 思考笔记
  标记复盘 / 请求增援 / 保存进度 / 完成
```

## 用户系统

```
Person (自然人)          User (账号)
  Id                       Id
  TrustLevel (0-5)         PersonId → Person
  TrustProgress            Platform + PlatformId
                           PermissionLevel (Blocked~Admin)
                           DisplayName / FastMemory

一个Person可关联多个User (跨平台)
Console平台用户默认Admin权限
```

## 指令系统

```
ICommand / IInteractiveCommand 接口
CommandSpawnCheck 拦截 → CommandRegistry 路由
前缀可配置 (CommandConfig.json)
命令: /help /status /whoami /memory /engine /wake /channel /user
      /test recall /note /persona /trust /config /reload adapter
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
│     └── EngineConfig.json   自启动引擎列表 {"autoStart":["Timer"]}
├── Adapter/
│     └── OneBotAdapter.json  QQ适配器配置(WS地址/白名单/botNames)
├── Command/
│     └── CommandConfig.json  指令前缀配置
├── PersonaMemorySeed.txt     人设记忆种子（首次启动导入）
├── Database/lilara.db        SQLite数据库
└── Logs/                     framework日志 + Model调用日志
```
