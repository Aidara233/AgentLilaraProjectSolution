# 上下文管理

## SummarizationCore（对话摘要）

**文件：** `Core/SummarizationCore.cs`
**继承：** `CoreBase`
**配置：** `Storage/Core/SummarizationCore.json`

### 定位

三层上下文压缩系统的核心组件，用于将对话历史压缩为简洁的结构化摘要。

### 实例化

| 位置 | 说明 |
|------|------|
| `CompressionTierModule` | 系统循环压缩模块 |
| `ContextCompressionModule` | 通用上下文压缩模块 |

每个模块持有一个 SummarizationCore 单例。

### 关键设计

- `UsePersona = false`（纯工具性）
- 构造函数中立即调用 `ApplyExtraMessages()`，通过配置中的 `conversationHistory` 注入系统提示词（压缩原则）
- 提示词包含 5 条压缩原则：保留决策/任务/便签、保留最近 5 轮工具调用、丢弃冗余、简洁概括

### SummarizeContextAsync

```csharp
Task<string> SummarizeContextAsync(
    List<Message> messages,       // 需要压缩的对话历史
    string? existingSummary = null // 现有摘要（增量压缩时传入）
)
```

增量模式：提供 `existingSummary` 时，模型在现有摘要基础上合并新内容，而非从头开始。

### 与压缩系统的关系

三层压缩（`CompressionTierModule`）：
- **L1（提示）**：摘要长度在阈值内，追加提示建议模型使用 `compress` 工具主动压缩
- **L2（提醒）**：超过软限制，SummarizationCore 自动执行一次摘要
- **L3（硬保底）**：超过硬限制，SummarizationCore 执行激进压缩，必要时截断
