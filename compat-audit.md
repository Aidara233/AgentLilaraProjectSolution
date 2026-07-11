# 兼容性审计报告

> 架构大换血后（统一 AgentCore + RuntimeSession 替代旧 ExpressCore/SessionCore/WorkingSession）的遗留问题汇总。
> 生成于 2026-07-01

---

## 严重：运行时隐患

### 1. SleepTalkCore.json 引用了不存在的 Persona.txt

| 项目 | 值 |
|------|-----|
| **文件** | `Storage/Core/SleepTalkCore.json`（模板同：`templates/Core/SleepTalkCore.json`） |
| **问题** | `promptFiles: ["Persona.txt", "SleepTalkPrompt.txt"]` — `Persona.txt` **不存在**于 `Storage/Core/Prompts/` 或 `templates/Core/Prompts/` |
| **表现** | `Processor.LoadBasePrompt()` 静默跳过不存在的文件，SleepTalkCore 无法加载人格设定，梦话上下文缺失 |
| **修复** | 从 `promptFiles` 移除 `"Persona.txt"`，或创建该文件 |

### 2. Plugin.WorkingTools 直接依赖 AgentCoreProcessor（破坏插件隔离）

| 项目 | 值 |
|------|-----|
| **文件** | `Plugins/Plugin.WorkingTools/Plugin.WorkingTools.csproj:14` |
| **问题** | `<ProjectReference Include="..\..\AgentCoreProcessor\AgentCoreProcessor.csproj" />` — 25 个插件中**唯一**一个直接依赖宿主项目的 |
| **牵连代码** | `ThinkingNotesTool.cs:15`、`PinboardTool.cs:15`、`TaskListTool.cs:16`、`RetainListTool.cs:17` — 四个工具实现了 `AgentCoreProcessor.Engine.IInjectProvider` |
| **影响** | 宿主内部接口变动时编译断裂；违反"插件只依赖 SDK"架构约定 |
| **修复** | 移除 `IInjectProvider` 实现（`WorkingToolsComponent.BuildPromptSection()` 已做同样的事），移除 csproj 的 ProjectReference |

---

## 中等：死代码 / 废弃基础设施

### 3. SystemEngine 已死但基础设施仍为其保留完整通道

整个组件系统对 `"system"` 引擎类型的支持完整保留，但**没有任何运行时引擎实际使用它**。

| 位置 | 存在 | 实际使用 |
|------|------|----------|
| `ComponentConfig.cs:73` → `EngineTypes` | `["channel", "system", "sub-agent", "review"]` | `"system"` **无引擎使用** |
| `ComponentConfig.cs:92` → `ComponentPermEntry` | `System` 属性 | **无引擎回传** |
| `ComponentRegistry.cs:74` → `GetLoopComponents` | `"system" => r.SystemApplicability` | **从未被调用** |
| `ConfigProvider.cs:20` → WebUI 配置列表 | `["SystemCore"] = "Core/SystemCore.json"` | 模板存在但**无代码加载** |
| `Storage/Engine/ComponentConfig.json` | `email/System: true, campus-print/System: true` | 条目对运行时无影响 |
| `SystemLoop/` 目录 | `state.json`, `context.jsonl`, `cross_requests.jsonl` | 可能由旧代码遗留 |

**6 个插件声明了 `System = Enabled`**（cross-loop, web-search-loop, browser-tools-loop, network-tools-loop, ssh-tools-loop, system-ops），如果未来有人创建 "system loop" 引擎会无预警激活。

| 建议 | 说明 |
|------|------|
| 最激进 | 清理所有 `"system"` 引用（`ComponentConfig.EngineTypes`、`ComponentRegistry` 的 `SystemApplicability`、`LoopApplicabilityAttribute.System`、`SystemCore.json` 模板） |
| 保守 | 保留基础设施但不新增文档引用，标记为"预留但未使用" |

---

## 中等：误导性引用

### 4. architecture_overview.txt 引用了不存在的文档路径

| 文件 | Storage | 模板 |
|------|---------|------|
| 位置 | `Storage/Core/Prompts/architecture_overview.txt:20` | `templates/Core/Prompts/architecture_overview.txt:20` |
| 原文 | `架构文档索引：arch/00-INDEX.md` | |
| 正确 | 应为 `docs/architecture-map.md` | |

这个提示文本会被注入到 Express/Working 两种核心的系统提示中，模型如果尝试访问这个路径会混淆。

### 5. ChannelEngine.cs 注释 "已移除 Working 模式" 产生误导

| 文件 | 行号 | 注释原文 | 问题 |
|------|------|----------|------|
| `ChannelEngine.cs` | 115 | `// Express/Working 自适应切换 — 已移除 Working 模式，仅保留 Express` | Working **没有被移除**——它从 ChannelEngine 内联模式移到了独立的 RuntimeSession 中 |
| `ChannelEngine.cs` | 125 | `// 模式配置驱动（Phase 2）：当前模式 ID 和定义 — 已移除，仅保留 Express` | 同上，Working 模式确实从 ChannelEngine 移除了，但整体系统中仍在 |
| | | | 应澄清：ChannelEngine 不再内联做 Working —— Working 由 RuntimeSession 独立处理 |

### 6. AgentCore 默认配置名叫 "SessionCore"

| 文件 | 行号 | 代码 |
|------|------|------|
| `AgentCore.cs` | 19 | `private string currentMode = "SessionCore";` |
| `AgentCore.cs` | 43 | `public AgentCore() : base("SessionCore")` |
| `AgentCore.cs` | 57 | `var target = mode == Engine.EngineMode.Express ? "ExpressCore" : "SessionCore";` |

"SessionCore" 是内部配置文件名，但在调试日志、WebUI 中会直接暴露给开发者。考虑改用 `"WorkingCore"` 更明确，或至少保持与 `EngineMode.Working` 的枚举值一致。不过改配置名需要同步改 `Storage/Core/SessionCore.json` 文件名，涉及面较广。

---

## 低：文档过期

### 7. architecture-map.md —— "WorkingSession" 出现 ~5 处

| 行号 | 原文 | 建议 |
|------|------|------|
| 99 | `escalate → 创建 WorkingSession` | `escalate → 创建 RuntimeSession（Working scene）` |
| 188 | `分发 → WorkingSession` | `分发 → RuntimeSession` |
| 226-228 | `WorkingSession` x2 | 同上 |
| 252 | `WorkingSession 创建时自动` | 同上 |

### 8. agent-core.md —— 引用 SystemEngine + 模式命名不一致

| 行号 | 问题 |
|------|------|
| 7 | `SystemEngine（一个）` — 不存在 |
| 18 | 模式表 `Session → SessionCore.json` — 枚举值是 `EngineMode.Working`，不是 "Session" |
| 41 | 描述用 `Working 模式` — 与表头 "Session" 不一致 |
| 70 | `new AgentCore("SystemCore", false)` — 无对应引擎 |
| 78 | `SystemEngine.cs:110` — 文件不存在 |

### 9. signal-instrumentation-guide.md —— SystemEngine x3

| 行号 | 问题 |
|------|------|
| 33 | `SystemEngine processes task` — 不存在 |
| 69 | `"System引擎"` 示例 — 不存在 |
| 193-194 | `SystemEngine` scope 命名 — 不存在 |

### 10. claude-quick-ref.md —— "system" 引擎类型

| 行号 | 问题 |
|------|------|
| 24 | CrossLoopTools 适用性包含 `system` — 无 system 引擎运行时 |

---

## 低：代码风格 / 一致性问题

### 11. RuntimeSession 硬编码组件循环类型

```csharp
// RuntimeSession.cs:51
private string _componentLoopType = "channel";
```

SessionEngine 创建的会话用于 Working/Task/Eavesdrop 等场景，但 `_componentLoopType` 始终是 `"channel"`。如果未来有非频道会话，组件过滤会失效。建议从 `CreateSessionSpec` 传参。

### 12. ChannelEngine.EngineType 大写 "Channel" vs 组件系统小写 "channel"

```csharp
// ChannelEngine.cs:32
public string EngineType => "Channel";
```

`ComponentConfig.EngineTypes` 用的是 `"channel"`（全小写）。虽然目前 `EngineType` 和组件系统不直接比较，但未来如果对比会出错。

### 13. RuntimeSession.EngineType 也被设为 "channel"（与 ChannelEngine 同名）

```csharp
// RuntimeSession.cs:495
_agentCore.EngineType = "channel";
```

Session 和 ChannelEngine 用同一个 EngineType 字符串，但需要不同的组件集。目前通过 `ComponentConfig.IsEnabled` 和 `LoopApplicability` 区分，但值得注意可能存在组件可见性冲突风险。

---

## 信息：仅观察

### 14. SystemLoop/ 存储目录存在

```
E:\Tool\Lilara\Storage\SystemLoop/
├── state.json
├── context.jsonl
├── cross_requests.jsonl
└── delegations.json
```

目录结构和数据可能是旧版系统循环的遗留物。建议确认是否可以清理或是否需要归档到 `Database/`。

### 15. Storage/Engine/ComponentConfig.json 条目不全

```json
{
  "Components": {
    "email": { "Channel": true, "System": true, "SubAgent": true, "Review": true },
    "campus-print": { "Channel": true, "System": true, "SubAgent": true, "Review": true }
  }
}
```

只有 2 个组件有显式配置项。其余插件都靠 `ComponentConfig.IsEnabled()` 的默认值（true）。虽然这是正常工作机制，但 WebUI `/p/plugins` 页只能编辑已存在的条目，无法添加新条目。

---

## 汇总

| 分级 | 数量 | 简述 |
|------|------|------|
| 🔴 严重 | 2 | SleepTalkCore 缺 Persona.txt；Plugin.WorkingTools 耦合宿主项目 |
| 🟡 中等 | 4 | SystemEngine 废弃遗留；文档路径错误；注释误导；配置名不一致 |
| 🟢 低 | 6 | 文档过期 (4)；代码风格 (2) |
| ℹ️ 信息 | 2 | SystemLoop 目录；ComponentConfig 条目不全 |
| **合计** | **14** | |

### 推荐优先级

1. **SleepTalkCore.json** 修复 `Persona.txt`（一行配置改动）
2. **Plugin.WorkingTools** 解耦（移除 `IInjectProvider` + `ProjectReference`）
3. **architecture-map.md** 更新 "WorkingSession" → "RuntimeSession"
4. **agent-core.md / signal-instrumentation-guide.md** 删除 SystemEngine 引用
5. **architecture_overview.txt** 修复文档路径
6. **ChannelEngine.cs** 注释修正
7. `ComponentConfig.EngineTypes` 中的 "system" 决策（保留/清理）
