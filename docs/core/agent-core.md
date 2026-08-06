# Agent 核心

## AgentCore（统一对话核心）

**文件：** `Core/AgentCore.cs`
**继承：** `CoreBase`
**实例化：** RuntimeSession（按需创建，会话化后频道响应由 express/working/task/review 会话驱动）

### 定位

AgentCore 是系统中最关键的核心，负责所有 LLM 对话交互。它合并了原先 ExpressCore + SessionCore 两个独立核心的能力，统一处理两种引擎模式下的模型调用。SessionCore（原 WorkingCore）现已专供 RuntimeSession 的统一会话循环使用。

### 核心配置映射

会话类型在 `Storage/Session/{type}.json` 声明 `DefaultCoreConfig`，各会话类型默认核：

| 会话类型 | 默认核配置 | 用途 |
|------|----------|------|
| express | `ExpressCore.json` | 快速响应（fire-and-forget 工具，不续轮） |
| channel | `ChannelCore.json` | 工作模式多轮推理（escalate 后接管频道） |
| task | `TaskCore.json` | 任务执行 / 子 agent |
| review | `TaskCore.json` | Review 验证会话（复用 TaskCore，ReviewQueue + review_* 工具链） |

模式通过 `SwitchMode(EngineMode)` 切换（EngineMode 为宿主侧概念，保留）。运行时换核走 `RuntimeSession.ConfigureCores / SwitchCore`（见下文"Session 多核切换"）。

### 关键属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `UseNativeTools` | `bool` | 从配置的 `useNativeTools` 读取，决定是否使用原生 tool_use |
| `AdditionalTools` | `List<ITool>?` | 当前轮次的 loop 组件工具 |
| `GlobalComponentTools` | `List<ITool>?` | 全局组件工具（所有轮次可见） |
| `SessionPrompt` | `string?` | 会话身份提示词（由 SessionTypeProfile.PromptFiles 构建，换核不换） |
| `EngineType` | `string?` | 当前权限组 key（express/channel/task/review） |

### 核心方法

#### InvokeAsync — 统一入口

```csharp
Task<ModelOutput> InvokeAsync(List<Message> messages, EngineMode mode)
```

根据 `mode` 分流：
- **Express 模式**：收集 Express 可用工具 → `UseNativeTools` 时走原生 tool_use，否则纯文本生成
- **Working 模式**：收集组件工具 → `UseNativeTools` 时走原生 tool_use，否则文本 JSON 解析

#### ChatAsync — 单次生成

```csharp
Task<string> ChatAsync(string input, List<string>? imagePaths)
```

Express 模式下的简单对话，支持多模态图片输入。

### 工具合并逻辑

`MergeTools(defs, tools)` — 将组件工具合并到工具定义列表，自动跳过 `ToolRegistry.IsDisabled` 的工具和重名工具。

### 构造函数变体

| 构造 | 说明 |
|------|------|
| `new AgentCore()` | 默认，配置="SessionCore"（遗留默认值；当前代码无调用点） |
| `new AgentCore(cfgName)` | 按名称加载 `Storage/Core/{cfgName}.json`，如 `new AgentCore("ChannelCore")`、`new AgentCore("TaskCore")` |

### 实例化位置

| 位置 | 构造 | CallerTag |
|------|------|-----------|
| `RuntimeSession`（SessionEngine.CreateSession） | `new AgentCore(spec.CoreConfigName)` | `"Session:{sessionId}"` |
| `RuntimeSession`（review 会话，`Storage/Session/review.json`） | `new AgentCore("TaskCore")` | `"Review:{mode}"` |

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

### Review 会话（复用 TaskCore）

`Storage/Session/review.json` 已补齐会话类型档案字段（Type/DefaultCoreConfig/AllowedCores/DefaultComponents/PromptFiles/PermissionGroup），配置形态与 channel/task 一致，`SessionTypeProfiles.Get("review")` 可正常加载（`All()` 也会枚举 review）。Review 验证会话**复用 TaskCore**（`DefaultCoreConfig: "TaskCore"`，AllowedCores 单核），旧 `ReviewCore.json` 已删。

ReviewEngine 已随记忆重构移除，Review 验证改走 `docs/memory-redesign/05` 流程：
- 工作源 = ReviewQueue（ReviewQueueService，唯一待验证队列）
- 工具链 = review_*（review_list/resolve/preview/commit/evidence，Plugin.ReviewTools）
- 提交 = ReviewStage git 式暂存（review_preview 预览影响 → review_commit 原子事务落库）

### 提示词归属（session 定身份，core 定模型）

会话身份提示词由 **session 类型配置的 `PromptFiles`** 声明（`Storage/Session/Prompts/`），不再由 core 配置提供：

- `SessionTypeProfile.PromptFiles` → `RuntimeSession.BuildSessionPrompt()` 拼接 → `AgentCore.SessionPrompt`，在 `SetConversationHistory` 覆盖 core 的 `BasePrompt`。
- `SwitchCore`/`ConfigureCores`/`SetActiveCore` 换核时把同一 `SessionPrompt` 绑定到新核 —— **换核只换模型配置执行器，会话身份提示词不变**。
- `PromptLoader.Load(file, storageRoot, ...)` 双目录搜索：`Session/Prompts`（session 主库）→ `Core/Prompts`（System/SleepTalk 等非 session 核心），templates 对应释放。
- 非 session 核心（System/SleepTalk）提示词仍跟 core 走（`Core/Prompts` + core 配置 `promptFiles`），无换核场景。
