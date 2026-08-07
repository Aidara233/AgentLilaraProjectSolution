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
| `forceToolCall` | bool | `false` | 强制 tool call（原生 tool_use 模式下） |
| `disableImageMessages` | bool | `false` | 关闭图片消息（多模态） |
| `enableCaching` | bool | `false` | 启用 prompt caching（`ShouldEnableCaching()` = enableCaching \|\| promptCaching） |
| `promptFiles` | string[] | `[]` | 非 session 核心提示词文件（`Core/Prompts/`，按序加载） |
| `extraBody` | dict? | `null` | 注入请求体的额外字段 |
| `conversationHistory` | List\<Message\> | `[]` | 预设对话历史（**已废弃，无读取点**，见文末） |
| `provider` | string | `"openai"` | 协议选择（见下方） |

### Claude/Anthropic 专属字段

| JSON 键 | 类型 | 默认值 | 说明 |
|---------|------|--------|------|
| `anthropicVersion` | string? | `null` | Anthropic API 版本头（如 `"2023-06-01"`） |
| `promptCaching` | bool | `false` | 启用细粒度 prompt caching（system/user 消息自动加 `cache_control`）——`enableCaching` 的兼容别名，任一为 true 即启用 |

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

Persona.txt 作为普通 prompt 文件处理。需要 Persona 的 Core 在 `promptFiles` 数组首项加入 `"Persona.txt"`，由 `PromptLoader` 统一按序加载。不需要 Persona 的 Core（工具性核心如 SleepTalkCore 等）不在 `promptFiles` 中包含即可。

> **session 会话的提示词不走 core 的 `promptFiles`**：channel/task/review 的身份提示词由会话类型配置（`Storage/SessionTypes/{type}.json` 的 `Prompts`，文件在 `Storage/SessionTypes/Prompts/`）决定，`RuntimeSession` 构建后经 `AgentCore.SessionPrompt` 注入，**换核不换提示词**。core 配置的 `promptFiles` 仅服务于非 session 核心（System/SleepTalk）。加载统一走 `PromptLoader` 双目录（SessionTypes/Prompts → Core/Prompts）。

---

## 当前配置分布

> 注：工作区 `Storage/Core/*.json` 的 `model`/`apiKey`/`provider` 多为**模板占位**（空字符串，由启动/部署时替换，如走 LIGHT 中转）。下表区分活跃核心与遗留文件。

**活跃核心配置：**

| 配置文件名 | provider | model | 用途 |
|-----------|----------|-------|------|
| `Base.json` | 模板占位 | 模板占位 | 默认回退（文件缺失时兜底） |
| `ExpressCore.json` | openai | deepseek-v4-flash | express 会话快速响应 |
| `ChannelCore.json` | openai | deepseek-v4-pro | channel 会话多轮推理（escalate 后接管） |
| `TaskCore.json` | openai | deepseek-v4-flash | task 会话执行 / 子 agent（review 会话复用此核） |
| `SleepTalkCore.json` | 模板占位 | 模板占位 | 梦话生成 |

**遗留配置（对应 Core 类已删除，Storage/Core 下的 json 已清理，2026-08-04）：** `CombineCore` / `ConsolidationCore` / `ConsolidationFinalCore` / `DedupCore` / `LinkCore` / `MemoryQueryCore` / `SummarizationCore` / `SystemCore`。对应模板保留在 `templates/Core/` 归档，如有需要可从模板重建。

> 记忆重构（2026-08-05）又删 `ReviewCore` / `MemoryExtractionCore` / `RelationClassificationCore`：提取/关系分类核心已并入心智引擎（MindEngine.ExtractService），review 会话复用 `TaskCore`（见 `Storage/SessionTypes/review.json`）。

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

> **⚠ 已废弃**：`conversationHistory`（映射 `ApiClientCfg.PresetMessages`）在代码中**无读取点**，是死字段。会话身份提示词改由 `Storage/SessionTypes/{type}.json` 的 `Prompts` 承载（经 `RuntimeSession.BuildSessionPrompt` → `AgentCore.SessionPrompt` 注入，换核不换）；非 session 核心用 `promptFiles` 数组加载 `Core/Prompts` 下的提示词文件。
