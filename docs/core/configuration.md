# Core 配置指南

## 配置文件位置

```
Storage/Core/{CoreName}.json
```

每个 Core 按其名称加载同名 JSON 配置。文件不存在时回退到 `Storage/Core/Base.json`。

## 完整配置字段

### 通用字段（所有协议）

| JSON 键 | 类型 | 默认值 | 说明 |
|---------|------|--------|------|
| `apiKey` | string | `""` | API 密钥 |
| `apiEndpoint` | string | `"https://api.siliconflow.cn/v1/chat/completions"` | API 端点完整 URL |
| `model` | string | `"deepseek-ai/DeepSeek-R1-Distill-Qwen-7B"` | 模型 ID |
| `temperature` | double | `0.7` | 采样温度 |
| `maxTokens` | int? | `null` | 最大输出 token 数 |
| `topP` | double? | `null` | 核采样参数 |
| `frequencyPenalty` | double? | `null` | 频率惩罚 |
| `presencePenalty` | double? | `null` | 存在惩罚 |
| `stream` | bool | `true` | 是否流式输出 |
| `n` | int | `1` | 候选回复数 |
| `useNativeTools` | bool | `false` | 是否使用原生 tool_use/function_calling |
| `extraBody` | dict? | `null` | 注入请求体的额外字段 |
| `conversationHistory` | List\<Message\> | `[]` | 预设对话历史（系统提示词等） |
| `provider` | string | `"openai"` | 协议选择（见下方） |

### Claude/Anthropic 专属字段

| JSON 键 | 类型 | 默认值 | 说明 |
|---------|------|--------|------|
| `anthropicVersion` | string? | `null` | Anthropic API 版本头（如 `"2023-06-01"`） |
| `promptCaching` | bool | `false` | 启用细粒度 prompt caching（system/user 消息自动加 `cache_control`） |

---

## 协议选择 (`provider`)

`provider` 字段是**唯一的协议切换开关**，在 `ModelClientFactory.Create()` 中决定使用哪个 SDK：

| `provider` 值 | 客户端类 | SDK |
|---------------|----------|-----|
| `"claude"` 或 `"anthropic"` | `ClaudeModelClient` | `Anthropic.SDK` |
| `"openai"` 或其他/省略 | `OpenAIModelClient` | `OpenAI` NuGet |

### OpenAI 协议配置示例

```json
{
  "apiKey": "sk-xxx",
  "apiEndpoint": "https://api.deepseek.com/v1/chat/completions",
  "model": "deepseek-v4-flash",
  "temperature": 0.7,
  "maxTokens": 4096,
  "stream": true,
  "useNativeTools": true
}
```

- `provider` 可省略（默认值 `"openai"`）
- `apiEndpoint` 填完整路径（含 `/v1/chat/completions`）
- 适用后端：DeepSeek、SiliconFlow、OpenAI 官方、任何 OpenAI 兼容 API

### Claude 协议配置示例

```json
{
  "apiKey": "sk-xxx",
  "apiEndpoint": "https://514claude.xyz/v1/messages",
  "model": "claude-opus-4-6",
  "provider": "claude",
  "temperature": 0.7,
  "maxTokens": 4096,
  "stream": true,
  "useNativeTools": true,
  "anthropicVersion": null,
  "promptCaching": true
}
```

- `provider` **必须设为** `"claude"`（或 `"anthropic"`）
- `apiEndpoint` 填 Anthropic 格式基路径（`/v1/messages`），SDK 自动拼接 `{base}/{version}/messages`
- `promptCaching` 启用时自动在 system/user 消息上添加 `cache_control`
- `extraBody.thinking` 可启用 Claude 扩展思考（`{"type": "enabled", "budget_tokens": 16000}`）

### 协议互换注意事项

切换 `provider` 时必须同步更新 `apiEndpoint`，否则 API 调用会 404：

| 切换方向 | Endpoint 变化 |
|----------|--------------|
| Claude → OpenAI | `/v1/messages` → `/v1/chat/completions` |
| OpenAI → Claude | `/v1/chat/completions` → `/v1/messages` |

协议专属功能在切换后**静默丢失**（不报错）：
- `promptCaching` → OpenAI 客户端忽略
- `anthropicVersion` → OpenAI 客户端忽略
- `extraBody.thinking` → OpenAI 客户端忽略
- Thinking 流事件 → OpenAI 客户端不发出

---

## Persona 注入

Persona.txt 作为普通 prompt 文件处理。需要 Persona 的 Core 在 `promptFiles` 数组首项加入 `"Persona.txt"`，由 `PromptLoader` 统一按序加载。不需要 Persona 的 Core（工具性核心如 SummarizationCore、MemoryExtractionCore 等）不在 `promptFiles` 中包含即可。

---

## 当前配置分布

| 配置文件名 | provider | model | useNativeTools | 用途 |
|-----------|----------|-------|:---:|------|
| `Base.json` | openai | deepseek-v4-flash | - | 默认回退 + 工具核心共用 |
| `WorkingCore.json` | claude | claude-opus-4-6 | ✓ | 频道循环 Working 模式 |
| `ExpressCore.json` | claude | claude-sonnet-4-6 | ✓ | 频道循环 Express 模式 |
| `SystemCore.json` | claude | claude-opus-4-6 | ✓ | 系统循环 |
| `ReviewCore.json` | openai | deepseek-v4-pro | ✓ | 复盘引擎 |
| `CombineCore.json` | openai | deepseek-v4-flash | - | 记忆组合 |
| `LinkCore.json` | openai | deepseek-v4-flash | - | 关联分析 |
| `ConsolidationCore.json` | openai | deepseek-v4-flash | - | 记忆整合第一轮 |
| `ConsolidationFinalCore.json` | openai | deepseek-v4-flash | - | 记忆整合第二轮 |
| `DedupCore.json` | openai | deepseek-v4-flash | - | 记忆去重 |
| `WeightCore.json` | openai | deepseek-v4-flash | - | 权重评估 |
| `MemoryExtractionCore.json` | openai | deepseek-v4-flash | - | 记忆提取 |
| `MemoryQueryCore.json` | openai | deepseek-v4-flash | - | （已废弃） |
| `SleepTalkCore.json` | openai | deepseek-v4-flash | - | 梦话生成 |
| `SubAgentCore.json` | openai | deepseek-v4-flash | - | （已废弃） |
| `SummarizationCore.json` | （特殊格式） | - | - | 摘要压缩 |

> 注：`VisionProvider.json`、`OcrProvider.json`、`EmbeddingProvider.json` 不是 Core 配置，而是独立 Provider 的配置。

---

## ExtraBody 用法

`extraBody` 用于向 API 请求体注入非标准字段，常见用途：

### DeepSeek 思考功能（OpenAI 协议）

```json
{
  "extraBody": {
    "thinking": { "type": "enabled" }
  }
}
```

### Claude 扩展思考（Claude 协议）

```json
{
  "extraBody": {
    "thinking": { "type": "enabled", "budget_tokens": 16000 }
  }
}
```

> 警告：`extraBody.thinking` **仅被 ClaudeModelClient 解析**。在 OpenAI 协议下，需要通过 `extraBody` 的原始注入机制（取决于中转站是否支持转发）。

---

## conversationHistory（预设消息）

`conversationHistory` 用于注入固定的系统提示词和预设对话：

```json
{
  "conversationHistory": [
    { "role": "system", "content": "你是一个记忆管理助手..." },
    { "role": "user", "content": "请开始处理。" }
  ]
}
```

消息在 `ApplyExtraMessages()` 中按顺序注入到客户端。
