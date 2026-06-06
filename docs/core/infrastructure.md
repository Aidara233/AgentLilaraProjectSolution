# 基础设施

## CoreBase（抽象基类）

**文件：** `Core/CoreBase.cs`
**抽象类**，所有 LLM 调用 Core 的根。

### 关键属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `CoreName` | `string` | 只读，值为 `GetType().Name`（派生类名） |
| `CallerTag` | `string?` | 调用来源标签，如 `"Channel:2"`、`"System"`、`"SubAgent:3"`，由调用方设置 |
| `CallLogRepo` | `static ModelCallLogRepository?` | 全局共享，由 MasterEngine 注入，所有 Core 写入调用日志 |

### 受保护成员

| 成员 | 类型 | 说明 |
|------|------|------|
| `processor` | `Processor` | 模型客户端包装器实例 |
| `extraMessage` | `List<Message>` | 预设消息列表，通过 `ApplyExtraMessages()` 注入 |
| `breakString` | `List<string>` | 流式生成断点标记，默认 `["<over>"]` |
| `UsePersona` | `virtual bool` | 是否注入 Persona，默认 `true`，子类覆盖为 `false` 跳过角色扮演 |

### 构造函数

```csharp
CoreBase()           // 无参构造，cfgName = CoreName（派生类名）
CoreBase(string cfgName)  // 指定配置文件名（不含 .json 后缀）
```

配置文件查找路径：`Storage/Core/{cfgName}.json`，不存在时回退到 `Base.json`。

### 生成方法

| 方法 | 返回值 | 说明 |
|------|--------|------|
| `GenerateAsync(onDelta?, onBreak?)` | `Task<Usage>` | 流式文本生成，支持 `<over>` 断点解析 |
| `GenerateOnceAsync()` | `Task<string>` | 单次文本生成（无参数，直接生成，依赖已有对话历史） |
| `GenerateOnceAsync(userMessage)` | `Task<string>` | 单次文本生成（注入一条 user 消息后生成） |
| `GenerateOnceAsync(userMessage, imagePaths)` | `Task<string>` | 多模态版本：文本 + 图片路径 |
| `GenerateWithToolsAsync(toolDefs, onEvent, ct)` | `Task<Usage>` | 原生工具调用流式生成 |

### 虚方法

| 方法 | 说明 |
|------|------|
| `OnDelta(ApiResponse)` | 每个增量响应到达时的钩子，子类可覆盖 |
| `OnBreak(ResponseBlock)` | 流式文本遇到断点时的钩子，子类可覆盖 |

### 日志记录

`LogOutput(content, reasoning, usage, isError)` — 每次调用自动执行：
- 将完整对话历史写入 `Storage/Logs/Model/{timestamp}_{CoreName}.json`
- 将 token 使用量写入数据库 `ModelCallLog` 表（带 `CallerTag`）

### 生命周期

`ResetProcessor()` — 重新创建 Processor（重置对话状态），保留 `extraMessage`。

---

## Processor（模型客户端包装器）

**文件：** `Core/Processor.cs`
封装 `IModelClient`，负责配置加载、客户端创建、Persona 注入和自动重试。

### 关键属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `Client` | `IModelClient` | 当前模型客户端实例（只读） |
| `CfgName` | `string` | 配置文件名，setter 触发从 `Storage/Core/{value}.json` 重新加载配置并创建新客户端 |
| `UsePersona` | `bool` | 是否注入了 Persona（构造时设定） |

### CfgName setter 行为

1. 拼接完整路径 `Storage/Core/{value}.json`
2. 文件不存在 → 回退到 `"Base"`
3. 读取 JSON → `ApiClientCfg.FromJson()` 反序列化
4. `ModelClientFactory.Create(cfg)` 创建客户端
5. `LoadBasePrompt()` 根据配置的 `promptFiles` 数组加载 .txt 提示词文件到 system prompt

### ProcessAsync

```csharp
Task ProcessAsync(Action<ApiResponse> onDelta, CancellationToken ct, Action? onRetryReset)
```

流式调用，失败时自动重试一次（先调用 `onRetryReset` 清空已累积内容）。

---

## ModelOutput（输出数据结构）

**文件：** `Core/ModelOutput.cs`
`readonly struct`，不可变。

| 字段 | 类型 | 说明 |
|------|------|------|
| `Text` | `string?` | 纯文本输出 |
| `Thinking` | `string?` | 思考过程（Claude 扩展思考 / 文本模式下的 think 内容） |
| `ToolCalls` | `List<ToolCall>?` | 工具调用列表 |

| 工厂方法 | 用途 |
|----------|------|
| `FromText(text)` | 纯文本结果 |
| `FromTools(calls, thinking?)` | 工具调用结果（Working 模式） |
| `FromExpressWithTools(text, calls, thinking?)` | Express 模式结果（文本 + 工具调用） |

| 判断属性 | 说明 |
|----------|------|
| `IsText` | Text 不为 null |
| `HasToolCalls` | ToolCalls 非空 |
