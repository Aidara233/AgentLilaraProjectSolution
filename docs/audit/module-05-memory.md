# 模块5审计报告：记忆系统 (Memory)

审计时间：2026-05-26
文件数：16 | 总行数：~1,800

---

## 发现问题

### 🔴 BUG — 中度

**1. MemoryService.RecallAsync O(N*M) 线性查找** (`MemoryService.cs:210-215`)
```csharp
var entry = mainEntries.FirstOrDefault(m => m.Id == s.Id);
```
`mainEntries` 是全量主库记忆列表（`mainResults.Select(x => x.Entry).ToList()`），对每个返回结果做 `FirstOrDefault` 线性扫描。若主库有 N 条记忆、返回 K 条结果，此为 O(N*K)。在有数万条记忆时，每次召回都额外做数万次比较。应改为 `Dictionary<int, MemoryEntry>`。

**2. TempMemoryRepository.ClearAllAsync 逐条删除** (`TempMemoryRepository.cs:75-82`)
```csharp
var all = await GetAllAsync();
foreach (var entry in all)
    count += await db.DeleteAsync(entry);
```
全量加载后逐条 DELETE，对大量临时记忆（几百条）是 N+1 模式。应直接 `DELETE FROM TempMemories`。

**3. SQL 拼接模式（多文件）** (`MemoryRepository.cs:67-69,127-128`; `MemoryLinkRepository.cs:23-25`; `TempMemoryRepository.cs:96-98`) ✅ 已修复 2026-05-26
`GetByIdsAsync` / `DeleteByIdsAsync` / `GetLinksForAsync` 使用 `$"SELECT ... WHERE Id IN ({idList})"` 字符串插值。虽然当前传参均为 `List<int>` 不会实际注入，但模式本身危险——假如将来有人改为接受外部输入，即构成 SQL 注入。

### 🟡 BUG — 轻度

**4. MemoryAccessImpl.DeleteTempAsync 全量加载再查** (`MemoryAccessImpl.cs:244-249`)
```csharp
var all = await tempMemories.GetAllAsync();
var entry = all.FirstOrDefault(m => m.Id == id);
```
应直接用 `db.GetByIdAsync<TempMemoryEntry>(id)`。大量临时记忆时此操作浪费内存。

**5. MemoryExtractionCore.ParseResults fallback 全标 high confidence** (`MemoryExtractionCore.cs:119-124`)
JSON 解析失败 + 内容不以 `[`/`{` 开头时，按行拆分并全部标记为 high confidence fact。模型输出非结构化文本（如自然语言解释）时，每行都被当作"事实"入库，产生垃圾记忆。

**6. MemoryQueryCore 仅存在于 worktree** (`Core/MemoryQueryCore.cs`)
此文件在 `.claude/worktrees/` 分支中存在但主源码中已不存在，且 grep 确认无任何引用。如果是有意废弃的，worktree 中应同步删除；如果是待合并功能，应合并。

### 🟠 设计问题 — 重度

**7. 全量扫描架构 — 核心检索路径无索引** (`MemoryService.cs:94,110`; `MemoryRepository.cs:22-43,201-215`)
`RecallAsync` 每次调用都执行 `GetAllAsync<MemoryEntry>()` 全量加载到内存，然后暴力计算余弦相似度。`FindSimilarAsync` 同样全量扫描。临时库因体量小可以接受，但主库增长到数万条后：
- 每次召回都加载整个 Memory 表 → 内存/IO 开销
- 暴力余弦相似度 O(N*dim) → CPU 开销
- 没有近似近邻搜索（HNSW/FAISS）、没有 DB 层索引（如 sqlite-vss）

当前设计文档写的是"先筛后搜"，但实现是"不筛全搜"。临时缓解：`GetAllWithMatchScoreAsync` 至少过滤了过期记忆，但未做任何候选缩减。

**8. RecallAsync 方法过长 — 6步骤内联** (`MemoryService.cs:76-218`)
~140 行方法包含 6 个步骤：临时库、主库、关联扩展、人设记忆、综合排序、更新访问时间。步骤 2（主库）的中间变量 `mainEntries` 跨了步骤 3/4/5 后才在步骤 6 使用。建议至少将关联扩展和人设记忆拆为独立方法。

### 🟢 ISSUE — 轻度

**9. MatchCount 评分粒度过粗** (`MemoryRepository.cs:34-37`; `TempMemoryRepository.cs:57-60`)
标签匹配分仅三项：personId 匹配 +1、channelId 匹配 +1、Knowledge 类型 +1（max 3）。无 subject 匹配、无加权差异（person 匹配应该比 Knowledge 类型匹配更有区分度）。

**10. MemoryAccessImpl.FilterAsync 双层过滤** (`MemoryAccessImpl.cs:74-101`)
先调 `GetAllWithMatchScoreAsync` 全量加载（已有 person/channel 软匹配），然后 LINQ 再做硬过滤（`m.PersonId == filter.PersonId`）。首轮软匹配的结果被后续硬过滤覆盖，且全量加载后内存中再做 LINQ 过滤是冗余的——应在 SQL 层就完成过滤。

**11. 记忆主库无 person/channel 索引** — `GetByPersonAsync` 和 `GetAllWithMatchScoreAsync` 都是全表扫描或 LINQ 过滤。若 SQLite 表上 `PersonId`/`ChannelId` 有索引，`GetByPersonAsync` 可走索引。但由于 `GetAllWithMatchScoreAsync` 读全表（无 WHERE），索引用不上。如果大部分查询是按 person 过滤的，加上 `WHERE PersonId = ?` 可减少数据量。

---

## 正面发现

- **MemoryService 检索流程设计合理**：6 步骤清晰（temp→main→links→persona→sort→touch），每一步职责明确
- **双库架构正确**：临时库充当"快速缓存"，主库存长期记忆，Persona 独立库
- **软匹配策略正确**：不做硬过滤，避免因 person/channel 不匹配而漏掉相关信息
- **评分公式均衡**：Similarity(0.5) + Importance(0.3) + Link(0.2) 权重分配合理，boosts 克制
- **Persona 降权设计好**：`PersonaPenalty=0.05` + 独立门槛 `PersonaMinScore=0.12` 确保人设记忆不淹没过真实记忆
- **关联扩展正确**：先取 topK 主记忆 → 查关联 → 排除已存在 → 计算 link 加权分数
- **MemoryExtractionCore 双段提取合理**：context 仅供理解（不从旧消息提取），newLines 才是提取源，防止重复
- **MemoryAccessImpl 桥接清晰**：SDK 接口 ↔ Repository 映射，DTO 转换独立方法
- **MemoryLink 双向匹配正确**：`GetLinksForAsync` 和 `CreateOrUpdateAsync` 都检查双向（SourceId↔TargetId）
- **CoreBase 子类极简**：6 个 Core 各 15-40 行，纯粹做 prompt 构造 + 模型调用，职责单一

---

## 判定

核心问题是**全量扫描架构**——`RecallAsync` 和 `FindSimilarAsync` 每次调用都加载整个 Memory 表。在记忆量达到数万时这将是性能瓶颈。短期内可接受（当前数据量不大），中期需要引入向量索引（如将 embedding 卸载到专用向量库）或至少加 DB 层预过滤。3 个 SQL 拼接模式需改为参数化查询。逐条删除 `ClearAllAsync` 应改为单条 DELETE。
