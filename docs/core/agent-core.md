# Agent 核心

## AgentCore（统一对话核心）

**文件：** `Core/AgentCore.cs`
**继承：** `CoreBase`
**实例化：** ChannelEngine（每频道一个）、SystemEngine（一个）、ReviewEngine（一个）

### 定位

AgentCore 是系统中最关键的核心，负责所有 LLM 对话交互。它合并了原先 ExpressCore + WorkingCore 两个独立核心的能力，统一处理两种引擎模式下的模型调用。

### 模式切换

| 模式 | 配置文件 | 用途 |
|------|----------|------|
| Express | `ExpressCore.json` | 快速响应（fire-and-forget 工具，不续轮） |
| Working | `WorkingCore.json` | 多轮推理（工具调用结果回注，循环执行） |

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
| `new AgentCore()` | 默认，配置="WorkingCore"，`UsePersona=true`，模式可切换 |
| `new AgentCore("SystemCore", usePersona: false)` | 系统循环用，固定模式，无 Persona |
| `new AgentCore("ReviewCore", usePersona: false)` | 复盘用，固定模式，无 Persona |

### 实例化位置

| 位置 | 构造 | CallerTag |
|------|------|-----------|
| `ChannelEngine.cs:73` | `new AgentCore()` | `"Channel:{id}"` |
| `SystemEngine.cs:110` | `new AgentCore("SystemCore", false)` | `"System"` |
| `ReviewEngine.cs:90` | `new AgentCore("ReviewCore", false)` | `"Review:{mode}"` |
