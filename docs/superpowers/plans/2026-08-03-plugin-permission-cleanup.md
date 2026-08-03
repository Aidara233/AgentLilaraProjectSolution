# 插件系统权限/引擎配置清理 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 落实"插件不参与任何权限/引擎配置"原则——插件侧所有权限与引擎适用性声明、运行时引擎自我判断、以及残留的废弃 system 循环概念，一律删除。谁能用、在哪用，完全由程序本体裁决（`PermissionService` + `Storage/Session/{type}.json` 工具黑白名单 + `Storage/Engine/ComponentConfig.json` 组件×权限组装配）。运行时核对另行安排，本计划只做删除。

**Architecture:** 删除三个方向的残留：
1. **声明式引擎适用性**：`[LoopApplicability]`（16 文件 17 处）、`[ToolMeta] EngineTypes`（SDK 属性 + 12 个 ReviewTools + 8 个宿主核心工具）、SDK `Applicability` 枚举与 `LoopApplicabilityAttribute`。程序本体的 `ComponentConfig.json` 已 100% 覆盖组件×三权限组装配（已逐项核对一致）。
2. **运行时引擎自我判断**：CrossLoopTools 的 `_ctx.LoopType == "system"` 守卫（使 `list_loops` 永不注册）、SkillTools 的 `IsAvailableFor(caller.LoopType)` 过滤 + `SKILL.md` `engines` frontmatter 引擎维度。
3. **废弃概念死代码**：SDK `ILoopControl` + SDK `EngineMode`（死接口，宿主无实现插件无消费）、`ToolRegistry.GenerateCapabilitySummary`（零调用）+ `CapabilitySummary` 属性（仅被死方法读取）、宿主 `LoopId.System/IsSystem/ExtractLoopType`（`ExtractLoopType` 零调用、`IsSystem` 仅被死守卫用）、`MasterEngine.WakeLoop` 的 IsSystem 死分支、`SignalFilterConfig` 的 `["system"]` 死条目、`EngineModule.GetTools(EngineMode)`（零调用）。

**保留（不在清理范围）：**
- 宿主 `AgentCoreProcessor.Engine.EngineMode`（Express/Working）——被 `AgentCore.SwitchMode/InvokeAsync`、`Agent`、`RuntimeSession`、`ExpressCycle` 使用，是活代码。
- `ToolMeta.Group`（纯 WebUI 分组）、`ContinueLoop`、`OutputOnly`（引擎追踪发过言，活）、`PluginDependency`。
- `PermissionService` + `Storage/Session/{type}.json`、`ComponentConfig.json`、`ToolConfig.json`（程序本体裁决源，不动）。
- `ToolMeta.CapabilitySummary` 之外：`McpBridgeTool` 自有 `CapabilitySummary` 属性（独立类，非 ToolMeta，保留）。
- 插件内工具自身的功能判定（`IConnectedChannel` 目标频道/发言能力、`ISpeakGuard`、NetworkTools 域名黑白名单）——工具自身能力，非"谁能用"。

**Tech Stack:** .NET 8 / C# / xUnit / AgentLilara Plugin SDK

## Global Constraints

- 编译走 solution 全量：`dotnet build AgentLilaraProject.sln`（不能只 build 单项目，`$(SolutionDir)` 依赖）
- 杀进程用 `cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null`（Git Bash 斜杠参数陷阱）
- 插件改完要 `dotnet build`（触发 `CopyToHostPlugins` MSBuild 目标）
- 注释、commit message 英文；类名/方法名/变量名英文；其他文档/讨论中文
- Storage 不在 solution 目录下，配置文件在 `Storage/` 目录
- **删除性改动**：SDK 删成员会让插件/宿主暂时编译不过——T1/T2/T3 文件互不相交，可并行；**每个子 agent 不做独立 build 验证**（最终统一 build），用 grep 自检"我的树内不再引用被删符号"。
- 数据操作（skill SKILL.md）：改前先备份（守则：确保操作可逆）
- 改动完成后按 CLAUDE.md 工作流走：build → commit → --test 试运行 → 文档

## 文件结构总览

### T1 — 插件文件清理（Plugins/ + Storage 技能数据）

| 文件 | 改什么 |
|------|--------|
| `Plugins/Plugin.BasicTools/BasicToolsComponent.cs` | 删 `[LoopApplicability]`；注释 express（ChannelEngine）→ ExpressEngine |
| `Plugins/Plugin.CrossLoopTools/CrossLoopComponent.cs` | 删 `[LoopApplicability]`；删 `if (_ctx.LoopType == "system")` 守卫 → 无条件注册 `list_loops` |
| `Plugins/Plugin.CrossLoopTools/ListLoopsTool.cs` | 删 `[ToolMeta] EngineTypes`（若有）、`CapabilitySummary`；描述去掉"仅系统循环可用" |
| `Plugins/Plugin.CrossLoopTools/SendMessageTool.cs` | 删 `CapabilitySummary`；描述去掉"填 system = 向系统循环发起" |
| `Plugins/Plugin.DicePool/DicePoolComponent.cs` | 删 `[LoopApplicability]` |
| `Plugins/Plugin.ExternalDice/ExternalDiceComponent.cs` | 删 `[LoopApplicability]` |
| `Plugins/Plugin.GitTools/GitComponent.cs` | 删 `[LoopApplicability]`（1 处） |
| `Plugins/Plugin.GroupFileTools/GroupFileToolsComponent.cs` | 删 `[LoopApplicability]` |
| `Plugins/Plugin.NetworkTools/BrowserToolsLoopComponent.cs` | 删 `[LoopApplicability]` |
| `Plugins/Plugin.NetworkTools/NetworkToolsLoopComponent.cs` | 删 `[LoopApplicability]` |
| `Plugins/Plugin.QuickActions/QuickActionsComponent.cs` | 删 `[LoopApplicability]` |
| `Plugins/Plugin.ReviewTools/ReviewToolsComponent.cs` | 删 `[LoopApplicability]` |
| `Plugins/Plugin.ReviewTools/Review*.cs`（12 个工具） | 删 `EngineTypes = new[] { "review" }`（保留 `Group = "review"`） |
| `Plugins/Plugin.ScheduledTasks/ScheduledTasksComponent.cs` | 删 `[LoopApplicability]` |
| `Plugins/Plugin.SkillTools/SkillToolsComponent.cs` | 删 `[LoopApplicability]`；删 `.Where(s => s.IsAvailableFor(caller.LoopType))` 过滤 |
| `Plugins/Plugin.SkillTools/SkillEntry.cs` | 删 `Engines` 字段 + `IsAvailableFor` + frontmatter `engines` 解析 |
| `Plugins/Plugin.SshTools/SshLoopComponent.cs` | 删 `[LoopApplicability]` |
| `Plugins/Plugin.SystemTools/SystemOpsComponent.cs` | 删 2 处 `[LoopApplicability]`；注释 WorkingSession → RuntimeSession |
| `Plugins/Plugin.WebSearch/WebSearchLoopComponent.cs` | 删 `[LoopApplicability]` |
| `Plugins/Plugin.WorkingTools/WorkingToolsComponent.cs` | 删 `[LoopApplicability]` |
| `Storage/PluginData/skill-tools/Skills/{casual-chat,code-review,system-maintenance}/SKILL.md` | 删 `engines:` frontmatter 行（先备份 3 文件到 `.superpowers/sdd/backup/`） |
| 其余所有插件工具文件 | 删 `[ToolMeta] CapabilitySummary = ...` 声明（grep 全量清扫） |

### T2 — SDK 契约清理（AgentLilara.PluginSDK/）

| 文件 | 改什么 |
|------|--------|
| `ToolMetaAttribute.cs` | 删 `EngineTypes` 属性、`CapabilitySummary` 属性（保留 Group/ContinueLoop/OutputOnly + PluginDependency） |
| `ComponentAttribute.cs` | 删 `LoopApplicabilityAttribute` 类 |
| `ComponentEnums.cs` | 删 `Applicability` 枚举（保留 ComponentScope/ShutdownReason/InitReason） |
| `Services/ILoopControl.cs` | 删文件 |
| `EngineMode.cs` | 删文件（SDK 这份死枚举；宿主 `AgentCoreProcessor.Engine.EngineMode` 保留） |

### T3 — 宿主清理（AgentCoreProcessor/）

| 文件 | 改什么 |
|------|--------|
| `Component/ComponentRegistry.cs` | 删 `GetCustomAttribute<LoopApplicabilityAttribute>` 读取、`ComponentRegistration` 的 3 个 Applicability 字段、构造参数 |
| `Component/ComponentConfig.cs` | 删 4 参 `IsEnabled(..., Applicability)` inert 重载（WebUI 改用真实 3 参版本）；清 `using` |
| `WebUI/Providers/PluginsProvider.cs` | 4 处 `reg.*Applicability` 读取 → 改从 `ComponentConfig.IsEnabled` 取真实状态；`:675` 标签 `"system"` → `"task"` |
| `Tool/Core/CompactTool.cs`、`ConnectChannelTool.cs`(×3)、`CoreTools.cs`(×2)、`EndSessionTool.cs`、`EscalateTool.cs`、`GetImageTextTool.cs`、`RefineImageTool.cs`、`SwitchCoreTool.cs` | 删 `EngineTypes = ...` |
| `Tool/ToolRegistry.cs` | 删 `GenerateCapabilitySummary` 死方法 |
| `Engine/Core/LoopId.cs` | 删 `System` 常量、`IsSystem`、`ExtractLoopType`（确认零调用） |
| `Engine/Core/MasterEngine.cs:876` | 删 `if (LoopId.IsSystem(loopId)) return;` 死守卫 |
| `Engine/Core/SignalFilterConfig.cs:151` | 删 `["system"]` 默认条目 |
| `Core/AgentCore.cs:47,:243` | 修过时注释（"system"/"sub-agent"、"系统循环专用"） |
| `Engine/Worker/EngineModule.cs:47` | 删 `GetTools(EngineMode)`（确认零调用） |

### T4 — 文档清理（docs/plugins/ + AGENTS.md + CLAUDE.md）

| 文件 | 改什么 |
|------|--------|
| `docs/plugins/README.md` | `:166` "已 inert" → "已删除"；`:119` WorkingTools "channel only"；`:125` CrossLoop "channel + task"；工具解析链/组件实例化段不引用 ChannelEngine |
| `docs/plugins/api-reference.md` | ToolMeta 属性表删 EngineTypes/CapabilitySummary/ExpressAvailable/AllowSubAgent/Permission 行；删 EngineMode 表；删 ILoopControl；`LoopType` 注释去 "system"/"sub-agent"；ISubAgentAccess"（系统循环用）"；`ChannelEngine` → RuntimeSession/ExpressEngine；InjectContext `Mode` 段；`:26` 组件实例化说明 |
| `docs/plugins/claude-quick-ref.md` | CrossLoop "channel + system"→channel；`ExpressAvailable` 示例；服务列表删 `ILoopControl`；`WorkingComponent.cs`→`WorkingToolsComponent.cs`；ToolPermission |
| `docs/plugins/quickstart.md` | 示例删 `Permission = ToolPermission.Default`、`ExpressAvailable = true` |
| `docs/plugins/path-config.md` | 引擎配置表核对 |
| `docs/plugins/patterns.md` | `ChannelEngine` 旧名引用核对 |
| `AGENTS.md`（工作目录） | `:32` "双循环：频道循环 + 系统循环" → 更新为当前架构 |
| `CLAUDE.md`（solution） | 核对插件相关行 |

## 任务依赖图

```
T1（插件清理） ─┐
T2（SDK 清理） ─┼─ 并行（文件互不相交）→ 统一 build + 修残留 → commit
T3（宿主清理） ─┘
                       ↓
                  T4（文档清理，代码落地后）
```

## 验收标准

1. `grep -rn "LoopApplicability\|EngineTypes\|ILoopControl\|EngineMode\|GenerateCapabilitySummary\|CapabilitySummary" Plugins AgentLilara.PluginSDK AgentCoreProcessor --include="*.cs"`（排除 bin/obj）除宿主 `EngineMode`、`McpBridgeTool.CapabilitySummary` 外无命中。
2. `grep -rn "system" CrossLoopComponent.cs` 无 `LoopType` 判断。
3. `Storage/PluginData/skill-tools/Skills/*/SKILL.md` 无 `engines:`。
4. `dotnet build AgentLilaraProject.sln` 0 error 0 warning。
5. `--test` 模式试运行通过。
6. 文档（T4）与实际代码一致。
