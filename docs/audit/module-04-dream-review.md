# 模块4审计报告：做梦+复盘 (Dream & Review)

审计时间：2026-05-26
文件数：12 | 总行数：~3,100（DreamEngine 1234行 + ReviewEngine 692行）

---

## 发现问题

### 🔴 BUG — 中度

**1. PrepareLinkAsync 对 oldest-dreamed 记忆空转标记** (`DreamEngine.cs:425-445`)
当通过 `GetOldestDreamedAsync` 获取的目标记忆没有 embedding（走 else 分支用 `GetRecentAsync` 粗筛），若 `filtered.Count == 0`，代码仍将 target 的 `LastDreamTime` 更新为当前时间后 return null。但由于 target 本身就是"最久未梦到的已梦记忆"，更新后其排序后移，下次又会被 `GetOldestDreamedAsync` 选中，形成"空转循环"——每次打开这个记忆都发现无候选、标记 dreamed、然后被下一次 oldest 选中。同一记忆可能被反复捡起做无用工。

**2. Daydream 无空闲时长阈值，默认冷却仅 120s** (`DreamEngineSpawnCheck.cs:108-114`)
Nap 有 `NapIdleThreshold`（默认 600s）限制最短空闲时长，而 Daydream 无对应阈值，仅检查 `IsIdle` + 冷却期。默认冷却 120s 意味着系统空闲时每 2 分钟就会触发一次走神（创建 DreamEngine、查 DB、建 session）。虽然单次开销小，但频率过高会在空闲期产生持续的 DB 和 API 开销。且 `DaydreamEnabled` 默认 false，一旦开启就会高频触发。

### 🟡 BUG — 轻度

**3. ParseFirstRoundResult 和 ApplyFinalResult 重复 ~70%** (`DreamEngine.cs:854-970`)
两个方法都做同一件事：解析 JSON 数组 → 遍历 keep/merge action → 处理 mergeWith 索引去重 → fallback 处理未处理项。区别仅在于数据源（`TempMemoryEntry` vs `ConsolidationCandidate`）和持久化方式（无操作 vs 写 DB + embedding）。可提取为泛型方法，参数化处理委托。

**4. ExecuteFragmentAsync 不检查 shouldWake（除 Consolidation）** (`DreamEngine.cs:538-577`)
`ExecuteWeightAsync`、`ExecuteLinkAsync`、`ExecuteCombineAsync`、`ExecuteDedupAsync` 均不检查 `shouldWake`。仅 `ExecuteConsolidationAsync` 在批次间检查（line 590, 604）。如果某片段执行时间较长（大模型调用），唤醒信号会被延迟到该片段完成后才响应。被唤醒后等待运行中片段的逻辑（line 279-290）是正确的，但唤醒延迟不可控。

**5. ReviewProgress.Findings / NextSteps 永远保存空列表** (`ReviewEngine.cs:320-335`)
`SaveProgress()` 每次将 `Findings` 和 `NextSteps` 序列化为 `new List<string>()`。这两个字段在 `ReviewProgress` 中定义并在 `BuildResumeContent()` 中恢复展示，但新版 ReviewEngine 从未写入它们。属于旧模式（ReviewModeSelector）的残留字段，当前 ReviewEngine 使用 `ThinkingNotes` 替代。读恢复时会展示空的"已有发现"和"待完成步骤"区块。

**6. TryCompressHistory 中 `notice = null!` 的误用** (`ReviewEngine.cs:454`)
```csharp
history[i] = notice;
notice = null!;  // 仅为了阻止后续 Insert，语义不清晰
```
用 null-forgiving 操作符来标记"已处理"是反模式。用 `bool inserted = false` 或 `noticeReplaced = true` 更清晰。

### 🟠 设计问题 — 中度

**7. ReviewEngine.TryCompressHistory 直接修改 Agent 内部状态** (`ReviewEngine.cs:354-462`)
压缩逻辑直接操作 `_agent.History`（`List<Message>`），修改其中 message 的 `ContentParts`，并在 conversationStart 位置插入/替换压缩提示消息。Agent 类暴露 `History` 为可读属性，但压缩逻辑深入到内部表示进行原地修改，耦合紧。如果 Agent 的内部消息格式（ContentParts 结构）变更，ReviewEngine 的压缩逻辑会同步破裂。

**8. DreamEngine.RunAsync Phase1→Phase2 逻辑内联在主循环中** (`DreamEngine.cs:248-271`)
信任评估 + ReviewEngine 启动的逻辑嵌入主循环的片段完成回调位置（~25行）。这段代码只在大睡且临时记忆首次清空时执行一次，但嵌在主循环内使 RunAsync 的方法职责混杂（调度循环 + 信任评估 + Review 启动）。

**9. ReviewEngine 不自动保存进度（崩溃丢失）** (`ReviewEngine.cs:109-179`)
`SaveProgress()` 仅在模型调用 `review_save_progress` 工具时触发。如果 ReviewEngine 异常退出（模型 500、进程被杀、token 预算意外耗尽），`EvaluationBuffer` 中的评价、`ThinkingNotes`、游标位置全部丢失。应在 `finally` 块中自动保存，或在每次 action 后自动快照。

### 🟢 ISSUE — 轻度

**10. CalcTimeWindowScore 默认 Peak 在窗口外缺注释** (`DreamConfig.cs:35,190-225`)
默认 `DeepSleepTimePeak = "00:00"`，而窗口是 `"02:00"` - `"06:00"`。Peak 在窗口外的结果是：整个窗口期都是下降段（从 Start 到尾单调递减）。这是一个合理的"鼓励早睡"设计——窗口早期评分最高，但配置默认值会让不熟悉归一化算法的读者困惑。应加注释说明。

**11. ExecuteTrustEvaluationAsync 每次都 Load ReviewConfig 文件** (`DreamEngine.cs:979-980`)
信任评估在 Phase2 阶段调用，此时 ReviewEngine 已构造并已持有同一份 `ReviewConfig`。重新从磁盘加载是冗余 IO。

**12. DreamEngine 使用 static Random 实例** (`DreamEngine.cs:43`) ✅ 已修复 2026-05-26
`private static readonly Random rng = new()` — 虽然当前保证同时只有一个 DreamEngine 实例，但 static Random 在多实例场景下不是线程安全的。如果将来允许并行做梦（如小睡+走神同时），会出问题。建议改为实例字段。

**13. ReviewEngine.BuildBudgetStatus 每轮重复注入** (`ReviewEngine.cs:655-665, 251-256, 280`)
`BuildBudgetStatus()` 在 `BuildStartInjectAsync`（首轮）和 `BuildRoundInjectAsync`（每轮）都调用。首轮注入后紧接着第一轮的 `BuildRoundInjectAsync` 会再次注入，造成首轮出现两条预算状态消息。浪费 token，且可能让模型困惑。

---

## 正面发现

- **DreamScheduler 设计优秀**：单线程串行调度，明确注释"不需要锁"。资源池 + 记忆冲突追踪 + 两层预算（主/增援），逻辑清晰
- **DreamConfig 非对称时间窗口评分**数学正确：归一化到 start-zero 消除跨午夜问题，上升段/下降段独立计算
- **ReviewEngine 压缩策略巧妙**：保留 action 工具结果、压缩导航工具结果，二次压缩时扩展到 action 结果，层次分明
- **EvaluationEngine 边界阻力公式**自然产生边际递减：`delta = (boundary - current) * rate * coefficient`，越接近边界变化越慢
- **SleepTalkCore 极简**：仅 28 行，纯粹委托给 CoreBase
- **DreamStats 7天滚动窗口设计合理**：自动修剪 + 基线重算，避免无限制增长
- **ReviewEngine 空转检测**到位：`_consecutiveNavRounds` 追踪纯导航轮次，超限后提示模型记录笔记
- **BudgetState 两层预算**：主预算耗尽停填充（`CanFill=false`），增援也耗尽才清空 todo（`ShouldClearTodo=true`），给调度留有余地

---

## 判定

整体质量良好，调度器设计是亮点。2 个中度 bug 需关注：PrepareLinkAsync 空转循环（浪费 API 调用）和 Daydream 无空闲阈值（开启后高频触发）。建议自动保存 Review 进度防止崩溃丢失。代码重复（ParseFirstRoundResult / ApplyFinalResult）可后续清理。
