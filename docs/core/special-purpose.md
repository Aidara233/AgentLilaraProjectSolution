# 专用核心

## SleepTalkCore（梦话生成）

**文件：** `Core/SleepTalkCore.cs`
**继承：** `CoreBase`
**配置：** `Storage/Core/SleepTalkCore.json`

### 定位

做梦系统的一部分。当 DreamEngine 完成一个做梦片段后，概率性地生成一段简短、梦幻的呓语（模拟角色在睡眠中的无意识话语）。

### 关键设计

- 通过 `LoadPromptTemplate()` 加载内置模板（`SleepTalkPrompt.txt`）生成呓语，不走 core 的 `promptFiles`
- 与其它记忆核心不同，它显式指定了配置文件名 `"SleepTalkCore"`（而非依赖 `CoreName` 自动匹配）

### GenerateAsync

```csharp
Task<string> GenerateAsync(List<string> fragments)  // 梦境片段列表
```

### 睡眠打断集成

- 走神期被 @ 不会触发梦话（仅标记唤醒）
- 小睡期被 @ 触发梦话响应但不唤醒，需 @ + 关键词才能叫醒
- 大睡期：不响应梦话

详见 `docs/architecture-map.md` 做梦系统（睡眠分级与打断）。

---

## PreprocessingCore（消息分类器）

**文件：** `Core/PreprocessingCore.cs`
**独立类，不继承 CoreBase**

### 定位

基于 Embedding 相似度的二分类器，判断用户消息是"任务请求"还是"闲聊"。不使用 LLM，纯数学计算。

### 工作原理

1. 使用 `bge-large-zh-v1.5` embedding 模型
2. 预置 10 条任务锚点句子（如"帮我读取这个文件"、"写一段代码"等）
3. 首次调用时，将 10 条锚点全部向量化并缓存
4. 对每条用户消息：
   - 计算消息向量与 10 个锚点的余弦相似度
   - 取最高相似度
   - 高于阈值（0.55）→ 判定为任务
   - 低于阈值 → 判定为聊天

### 依赖注入

```csharp
new PreprocessingCore(IEmbeddingProvider embedding)
```

通过构造函数注入 `IEmbeddingProvider`（SiliconFlow 的 bge-large-zh-v1.5），与其他 Core 的直接 `new()` 模式不同。

### 容错

Embedding 调用失败时默认返回 `false`（判定为聊天），不会阻断流程。

### 分类结果用途

> **⚠ 死代码**：`PreprocessingCore` 在当前代码中**无任何调用点**（全仓仅类定义）。原设计影响频道循环行为决策，而频道循环已在 2026-08-04 express 会话化重构中删除。若需消息分类能力，需重新接线到 express/working 会话。
