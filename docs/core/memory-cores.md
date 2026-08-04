# 记忆处理核心

记忆处理核心覆盖记忆的提取与关系分类。`MemoryExtractionCore` 由 `ExtractionEngine` 的 `ChannelExtractionWorker` 触发（每次提取批次临时创建）；`RelationClassificationCore` 由 `DreamEngine` 在睡眠期调度（秩序/巡逻阶段），也由实时写入异步触发。

记忆核心为纯工具性（无角色扮演），通过 `ResetProcessor()` 确保每次调用独立的对话上下文。

---

## MemoryExtractionCore（记忆提取）

**文件：** `Core/MemoryExtractionCore.cs`
**实例化：** `ChannelExtractionWorker`（每次提取批次临时创建）
**配置：** `Storage/Core/MemoryExtractionCore.json`

### 功能

从对话中提取值得记住的事实和反馈（knowledge / fact / feedback / inference / event / state / preference），带 initial certainty 标记。

### 双段提取

```csharp
Task<List<ExtractionResult>> ExtractAsync(
    List<string> contextLines,   // 参考上下文（旧消息，不从中提取）
    List<string> newLines,       // 新消息（从中提取）
    List<string>? recentMemories // 已记录信息（防重复）
)
```

---

## RelationClassificationCore（关系分类）

**文件：** `Core/RelationClassificationCore.cs`
**实例化：** `DreamEngine`（每睡眠 session 一个）、`MemoryAccessImpl`（实时异步）
**配置：** `Storage/Core/RelationClassificationCore.json`

### 功能

判断一个中心节点与多个候选目标节点的语义关系，输出 support 值（-1.0~1.0）。正值为关联/一致，负值为矛盾，接近 0 为无关。

**调用场景：**
- **秩序阶段**：temp 入库时，每条 temp 作为中心，其主库 top-K 候选作为目标
- **巡逻阶段**：三角闭合发现的无边邻居对，加入缓冲后批量调用
- **实时**：外部工具写入主库后异步触发

### prompt cache 策略

指令 + 输出格式前缀在同一个 dream session 内被多次复用（缓存命中），仅候选列表每轮替换。分多轮、每轮专注一个中心 + 最多 8 个候选。

### 输出解析

JSON 数组：`[{"targetIndex": N, "support": -1.0~1.0}]`。无关的候选可省略不输出。

---

## 已废弃

以下 Core 已被上述核心取代，文件已删除：

- **ConsolidationCore** — 两轮 LLM 整合被 embedding 去重 + RelationClassificationCore 取代
- **ConsolidationFinalCore** — 同上
- **LinkCore** — 被 RelationClassificationCore 取代
- **DedupCore** — 被 embedding 去重取代
- **CombineCore** — 记忆组合被砍掉（揉记忆与网结构矛盾）
