# 流事件处理器

两个处理器负责解析原生 `tool_use` 流事件（`StreamEvent`），将分散的 `ToolUseStart → ToolUseDelta → ToolUseEnd` 事件重组为完整的 `ToolCall` 对象。两者的关键区别在于对 `Text` 事件的语义解释。

---

## NativeToolCallHandler（Working 模式）

**文件：** `Core/NativeToolCallHandler.cs`
**使用方：** `AgentCore.GenerateWithNativeToolsAsync()`

### 定位

Working 模式下的工具调用流处理器。在 Working 模式中，模型不应输出自由文本（所有输出应为工具调用），因此 `Text` 事件被视为 thinking（思考内容）。

### 事件处理

| 事件类型 | 处理方式 |
|----------|----------|
| `Thinking` | 累积到 `thinkingParts` |
| `ToolUseStart` | 记录 `toolUseId` + `toolName`，清空参数缓冲区 |
| `ToolUseDelta` | 累积参数 JSON 片段 |
| `ToolUseEnd` | 调用 `FinalizeCurrentCall()` 完成解析 |
| `Text` | **视为 thinking**（Working 模式不应有自由文本） |

### ToolCall 解析

`FinalizeCurrentCall()` 中：
1. 将累积的 JSON 参数按 `ToolDefinition.Parameters` schema 映射为 positional `Inputs[]`
2. 映射策略：遍历 schema 的 `properties` 键，按定义顺序匹配 JSON 中的值
3. 无 schema 时按 JSON 属性顺序填入
4. 解析失败时整段 JSON 作为第一个参数

---

## ExpressToolCallHandler（Express 模式）

**文件：** `Core/ExpressToolCallHandler.cs`
**使用方：** `AgentCore.GenerateExpressWithToolsAsync()`

### 定位

Express 模式下的流处理器。Express 模式中，模型可能同时输出文本回复和工具调用（如先说一句话再调用 `send_media`），因此 `Text` 事件被视为**模型的正式回复内容**而非 thinking。

### 与 NativeToolCallHandler 的区别

| 方面 | NativeToolCallHandler | ExpressToolCallHandler |
|------|----------------------|------------------------|
| `Text` 事件语义 | **thinking**（思考） | **模型回复文本** |
| 输出结构 | `(Calls, Thinking)` | `(Text, Calls, Thinking)` |
| 文本累积 | `thinkingParts` | `textParts`（独立累积） |
| 使用场景 | Working 模式（多轮工具调用） | Express 模式（fire-and-forget + 可能说话） |

`Thinking` 事件在两者中独立处理，始终累积到 `thinkingParts`。

### GetResult 返回值

```csharp
(string Text, List<ToolCall> Calls, string? Thinking) GetResult()
```

三个独立维度：文本回复、工具调用列表、思考内容。
