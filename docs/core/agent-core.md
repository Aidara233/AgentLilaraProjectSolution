# Agent 核心

## AgentCore（统一对话核心）

**文件：** `Core/AgentCore.cs`
**继承：** `CoreBase`
**实例化：** ChannelEngine（每频道一个）、RuntimeSession（按需创建）、ReviewEngine（一个）

### 定位

AgentCore 是系统中最关键的核心，负责所有 LLM 对话交互。它合并了原先 ExpressCore + SessionCore 两个独立核心的能力，统一处理两种引擎模式下的模型调用。SessionCore（原 WorkingCore）现已专供 RuntimeSession 的统一会话循环使用。

### 模式切换

| 模式 | 配置文件 | 用途 |
|------|----------|------|
| Express | `ExpressCore.json` | 快速响应（fire-and-forget 工具，不续轮） |
| Session | `SessionCore.json` | 多轮推理（工具调用结果回注，循环执行；RuntimeSession 专用） |

模式通过 `SwitchMode(EngineMode)` 自动切换，切换时重建 Processor 以加载不同配置。若构造时指定了 `cfgName`（固定模式），则 `SwitchMode` 不生效。

### 关键属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `UseNativeTools` | `bool` | 从配置的 `useNativeTools` 读取，决定是否使用原生 tool_use |
| `ProfileManager` | `ToolProfileManager?` | 工具 Profile 管理器（由引擎注入） |
| `AdditionalTools` | `List<ITool>?` | 当前轮次的 loop 组件工具 |
| `GlobalComponentTools` | `List<ITool>?` | 全局组件工具（所有轮次可见） |

### 核心方法

#### InvokeAsync — 统一入口

```csharp
Task<ModelOutput> InvokeAsync(List<Message> messages, EngineMode mode, string? profileName)
```

根据 `mode` 分流：
- **Express 模式**：收集 Express 可用工具 → `UseNativeTools` 时走原生 tool_use，否则纯文本生成
- **Working 模式**：通过 `ProfileManager` 获取工具定义 → `UseNativeTools` 时走原生 tool_use，否则文本 JSON 解析

#### ChatAsync — 单次生成

```csharp
Task<string> ChatAsync(string input, List<string>? imagePaths)
```

Express 模式下的简单对话，支持多模态图片输入。

#### InvokeWithHistoryAsync — 系统循环专用

```csharp
Task<ModelOutput> InvokeWithHistoryAsync(List<Message> messages, string? profileName)
```

复用 Processor 实例（不重建），直接设置历史并调用。用于系统循环的高频调用场景。

### 工具合并逻辑

`MergeTools(defs, tools)` — 将组件工具合并到工具定义列表，自动跳过 `ToolRegistry.IsDisabled` 的工具和重名工具。

`IsExpressAvailable(ITool)` — 检查工具是否标记了 `ToolMetaAttribute.ExpressAvailable = true`。

### 构造函数变体

| 构造 | 说明 |
|------|------|
| `new AgentCore()` | 默认，配置="SessionCore"，`UsePersona=true`，模式可切换 |
| `new AgentCore("SystemCore", usePersona: false)` | 系统循环用，固定模式，无 Persona |
| `new AgentCore("ReviewCore", usePersona: false)` | 复盘用，固定模式，无 Persona |

### 实例化位置

| 位置 | 构造 | CallerTag |
|------|------|-----------|
| `ChannelEngine.cs:73` | `new AgentCore()` | `"Channel:{id}"` |
| `RuntimeSession`（SessionEngine.CreateSession） | `new AgentCore(spec.CoreConfigName)` | `"Session:{sessionId}"` |
| `ReviewEngine.cs:90` | `new AgentCore("ReviewCore", false)` | `"Review:{mode}"` |

### Session 多核切换（运行时）

Session 与 Core 解耦：session 一组配置文件（决定组件/上下文），core 一组配置文件（决定模型）。
每个会话类型在 `Storage/Session/{type}.json` 声明 `AllowedCores` 白名单（缺省 = 仅默认核，行为不变）。

- **白名单配置**：`channel.json` 的 `"AllowedCores": ["ChannelCore", "ExpressCore"]`；`task.json` 单核 `["TaskCore"]`。加载器见 `SessionTypeProfiles.Load`。
- **切换入口**（三路）：
  - 模型工具：`switch_core(name)` — 会话多核时自动注入工具表，模型自主升级/降级核。
  - 频道命令：`!core`（查看）/ `!core <name>`（切换）— 按频道 Full 占用解出会话。
  - WebUI：会话页「切换核心」表单（core 列 + sessionId/coreName 下拉）。
- **机制**：`RuntimeSession.ConfigureCores`（白名单 + 默认核 + 可选 activeOverride）取代旧 `SetAgentCore`；`SwitchCore` 校验白名单 → 懒建核实例缓存 → 换 `_agentCore` → 经 `_pendingInjections` 注入切换通知（下一轮 flush，防被轮内格式化覆盖）→ 持久化 `state.json.activeCoreConfig`。
- **持久化**：`state.json` 加 `activeCoreConfig` 字段，重启/compact 承接活跃核；`CreateFromCompact` 用 `SetActiveCore` 静默继承。
- **单核类型**（task 默认）不注入 `switch_core`、无切换入口，完全零回归。
- **轮内硬化**：`RuntimeSession` 每轮锁 `roundCore = _agentCore`，格式化标志（UseNativeTools 等）与产出该轮的核一致，外置切换不破坏进行中的轮。

### Review 对齐状态（待重构，配置已就位）

`Storage/Session/review.json` 已补齐会话类型档案字段（Type/DefaultCoreConfig/AllowedCores/DefaultComponents/PermissionGroup），配置形态与 channel/task 一致。**运行时尚未接入**：

- `SessionType` 枚举仍只有 `Channel/Task`，`SessionTypeProfiles` 只加载这两类文件——review.json 的档案字段当前是惰性占位（`PermissionService` 只取权限部分）。
- `ReviewEngine.cs:86` 仍硬编码 `new AgentCore("ReviewCore")`，不走白名单、不可运行时换核。

**待办**（Review 重构时实施）：枚举加 `Review` → `SessionTypeProfiles` 加 `Review` 档案 → `ReviewEngine` 改走 `ConfigureCores`。届时 review 复用与 channel 同构的白名单机制（如"快速浏览 flash / 深度评价 pro"双核）。
