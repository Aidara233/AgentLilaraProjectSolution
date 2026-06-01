# 记忆处理核心

记忆处理核心覆盖记忆的完整生命周期：提取 → 去重 → 权重评估 → 关联分析 → 组合推理 → 整合入库。除 `MemoryExtractionCore` 在频道循环中触发外，其余均由 `DreamEngine` 在睡眠期调度。

所有记忆核心均设置 `UsePersona = false`（纯工具性，无角色扮演），通过 `ResetProcessor()` 确保每次调用独立的对话上下文。

---

## MemoryExtractionCore（记忆提取）

**文件：** `Core/MemoryExtractionCore.cs`
**实例化：** `ChannelExtractionWorker`（每次提取批次临时创建）
**配置：** `Storage/Core/MemoryExtractionCore.json`

### 功能

从对话中提取值得记住的事实和反馈（knowledge / fact / feedback / inference / event），带置信度标记。

### 双段提取

```csharp
Task<List<ExtractionResult>> ExtractAsync(
    List<string> contextLines,   // 参考上下文（旧消息，不从中提取）
    List<string> newLines,       // 新消息（从中提取）
    List<string>? recentMemories // 已记录信息（防重复）
)
```

旧消息仅用于理解背景，新消息是实际提取源，已记录信息用于避免重复提取。

### 输出解析

优先 JSON 解析为 `List<ExtractionResult>`，带 markdown 代码围栏剥离。
JSON 解析失败时：若内容以 `[` 或 `{` 开头则返回空（避免碎片），否则按行 fallback。

详见 `ExtractionResult` DTO（同文件内定义）。

---

## ConsolidationCore（临记整合第一轮）

**文件：** `Core/ConsolidationCore.cs`
**实例化：** `DreamEngine` 内联字段（每睡眠会话一个）
**配置：** `Storage/Core/ConsolidationCore.json`

### 功能

模型判断每组临时记忆应如何处理：保留（keep）、合并（merge）还是丢弃（discard）。输入为临时记忆列表 + 已有主库记忆（用于去重参考）。

### 调用时机

仅在小睡/大睡时由 DreamEngine 调度执行。

---

## ConsolidationFinalCore（临记整合第二轮）

**文件：** `Core/ConsolidationFinalCore.cs`
**实例化：** `DreamEngine` 内联字段
**配置：** `Storage/Core/ConsolidationFinalCore.json`

### 功能

对第一轮各批次产出的 `ConsolidationCandidate` 列表进行跨组去重和最终合并决策。同时定义 `ConsolidationCandidate` DTO（携带 PersonId、ChannelId、Type、Subject、Certainty 等元数据）。

---

## DedupCore（记忆去重）

**文件：** `Core/DedupCore.cs`
**实例化：** `DreamEngine` 内联字段
**配置：** `Storage/Core/DedupCore.json`

### 功能

通用去重核心。接受任意格式的输入文本（种子记忆 + 关联集群），输出去重决策（merge/discard）。

---

## WeightCore（权重评估）

**文件：** `Core/WeightCore.cs`
**实例化：** `DreamEngine` 内联字段
**配置：** `Storage/Core/WeightCore.json`

### 功能

模型评估一批记忆的重要性，返回每条记忆的新 `Importance` 值（0.0-1.0）。低于阈值的记忆会被标记为可过期（后续由清理逻辑处理）。

输入包含当前 Importance 值供模型参考："当前重要性=0.75"。

---

## LinkCore（关联分析）

**文件：** `Core/LinkCore.cs`
**实例化：** `DreamEngine` 内联字段
**配置：** `Storage/Core/LinkCore.json`

### 功能

模型分析一条新记忆与候选记忆之间的语义关联关系。输出 JSON 数组，每项包含：候选记忆索引、关联类型、强度。

---

## CombineCore（记忆组合）

**文件：** `Core/CombineCore.cs`
**实例化：** `DreamEngine` 内联字段
**配置：** `Storage/Core/CombineCore.json`

### 功能

从一组强关联记忆中抽象推理，生成新的"衍生记忆"（新洞察/结论）。输出为衍生记忆的文本内容，或 `"none"` 表示无法产生有价值的组合。
