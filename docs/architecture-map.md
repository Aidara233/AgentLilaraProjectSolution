# Agent Lilara 架构地图

多平台 AI Agent 框架。核心：Engine 生态（OS 内核式调度）+ 记忆系统（双库 + 向量检索）+ 做梦机制（离线记忆整理 + 全局复盘）。

技术栈：.NET 8 / C# / SQLite-net-pcl / DeepSeek-V3.2 / SiliconFlow API / bge-large-zh-v1.5 embedding

## 目录结构

```
AgentCoreProcessor/
├── Adapter/     平台适配（Console/File），消息收发
├── Client/      LLM API 客户端，流式调用，Embedding 接口
├── Config/      PathConfig 绝对路径管理
├── Core/        业务核心（13个），继承 CoreBase，各自 JSON 配置
├── Database/    实体 + Repository（SQLite，9张表）
├── Engine/      引擎生态（MasterEngine 内核 + 子引擎）
├── Memory/      MemoryService 检索管线
├── Models/      API 请求/响应数据结构
├── Tool/        工具接口 + DAG 执行器 + 全局/局部工具集
├── Util/        VectorUtil 向量操作
└── Program.cs   入口（--file / --debug / 默认 Console）
```

## 引擎生态

```
MasterEngine (内核，实现 ISystemContext)
  ├── SpawnCheck 注册表 (IEngineSpawnCheck)
  │     Timer  → TimerEngine (心跳30s，IsInfrastructure=true)
  │     Worker → WorkerEngine (每条消息一个实例)
  │     Dream  → DreamEngine (走神/小睡/大睡)
  ├── 活跃引擎表 (List<ISubEngine>)
  └── 事件流水线: EventBus → HandleEventAsync
        ① 内核更新 (lastMessageTime)
        ② SpawnCheck: OnEvent → ShouldSpawn → Create → StartEngine
        ③ 派发 OnEvent 给活跃实例
        ④ 清理 IsAlive=false 的实例
```

ISubEngine: EngineType / RunAsync / OnEvent / IsAlive / RequestStop / IsInfrastructure(默认false)
ISystemContext: 数据访问 + 适配器 + EventBus + 引擎查询 + StartEngine/RequestStopEngine

## 消息处理流

```
Adapter → EventBus(MessageEvent) → WorkerEngineSpawnCheck → WorkerEngine
  ① SessionManager.OnMessageAsync (用户映射 + 话题分类 + 消息入库)
  ② 权限检查 (Blocked/Restricted 拦截)
  ③ PreprocessingCore 分类 (1-4级)
  ④ MemoryService.RecallAsync 检索记忆
  ⑤ 路由:
     1-2级(简单) → ExpressCore 直接回复
     3-4级(复杂) → WorkingCore Agent 循环 (多轮工具调用)
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

五种片段: Consolidation / Weight / Link / Combine / Review(独立引擎)

ReviewEngine (由DreamEngine孵化，不注册SpawnCheck):
  独立Agent循环 + 9个专用工具 + 4种复盘模式
  Token预算制: 基础预算 + 显式请求备用 + 预算外收尾
  产出写临时记忆 → 下次Consolidation整合
```

## 记忆系统

```
写入: 工具"记忆" → MemoryService.StoreAsync → TempMemory (自动生成embedding)
检索: MemoryService.RecallAsync
  ① TempMemory 全量扫描 (小库，快)
  ② Memory 标签过滤 (Person/Channel/Topic)
  ③ 向量精排 (cosine similarity)
  ④ 关联扩展 (MemoryLink)
  ⑤ 综合排序: Similarity×0.5 + Importance×0.3 + Link×0.2 + TempBoost×0.1
整合: TempMemory → Consolidation(做梦) → Memory
标记: ReviewHint表 (工作时标记 → 复盘时消费)
```

9张表: Person / User / Channel / Topic / UserMessage / MemoryEntry / TempMemoryEntry / MemoryLink / ReviewHint

## 工具系统

```
ITool: Name / Description / Parameters / Timeout / ExecuteAsync
ToolCall(JSON): tool + toolId + inputs(value/ref) + output + outputToModel + retain
ToolExecutor: DAG拓扑排序 + 分波并行 + 寄存器(跨轮) + 可选toolResolver

全局工具 (ToolRegistry, WorkerEngine用):
  文件流读取器 / 文件流写入器 / 说话 / 完成 / 思考笔记 / 记忆
  睡眠许可 / 强制睡觉 / 修改睡眠配置 / 调整睡意 / 触发红色警报 / 标记复盘

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
                           FastMemory (用户画像)

一个Person可关联多个User (跨平台)
Console平台用户默认Admin权限
```

## 配置文件

```
Storage/
├── Core/*.json          各Core的LLM配置+系统提示词 (11个)
├── Dream/
│     ├── DreamConfig.json    做梦调度参数+预算配置
│     ├── DreamStats.json     滚动7天统计+自适应基线
│     └── DreamProgress.json  复盘进度存档
├── Engine/
│     └── EngineConfig.json   自启动引擎列表 {"autoStart":["Timer"]}
├── Database/lilara.db        SQLite数据库
├── FileAdapter/              input.txt / output.txt
└── Logs/                     framework日志 + Model调用日志
```
