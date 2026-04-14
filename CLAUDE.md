# Agent Lilara — AI 协作指引

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
- 新工具：实现 ITool，全局工具注册到 ToolRegistry，引擎专用工具在引擎内部管理
- 新 Core：继承 CoreBase，配置放 Storage/Core/{CoreName}.json
- 新实体：放 Database/，Repository 模式，DbManager.InitAsync() 加 CreateTableAsync
- 基础设施引擎：设 IsInfrastructure => true（不影响 IsIdle 判定）

## 工作流（硬性要求）

每轮代码改动完成后，必须立即执行：
1. `dotnet build` 确认编译通过
2. `git commit` 提交改动
3. `--test` 模式试运行验证（需要模拟对话节奏时加 `--delay N`）

不要等用户提醒，做完改动就走这三步。

## 关键路径

- 入口：Program.cs（--file / --debug / 默认 Console）
- 引擎内核：Engine/MasterEngine.cs
- Agent 循环：Core/WorkingCore.cs
- 记忆检索：Memory/MemoryService.cs
- 做梦调度：Engine/DreamEngineSpawnCheck.cs
- 配置文件：Storage/ 目录下
