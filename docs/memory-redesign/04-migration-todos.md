# 迁移与待定

> 记忆重构计划 · 文件 04
> 状态：**已实施（历史对照）** — 2026-08-05。实施路线（阶段 0-6）已完成，旧系统已整体移除；本文保留迁移对照与决策记录供追溯。

## 与旧系统的对照

| 旧组件 | 新设计 |
|---|---|
| Memories 表 | Claims 表（三元组断言） |
| TempMemories 表 | 无（单库模式） |
| MemoryLinks 表 | 无（关键词承担关联；支持/反对边承担断言间关系，见 05） |
| PersonaMemories 表 | 合并到 Claims/Journals 或独立表 |
| MemoryService/RecallAsync | 新检索服务（关键词+时间窗口 + 可信度门控） |
| MemoryExtractionCore | 新提取逻辑 |
| 矛盾边（Support 负值） | 冲突组 + 支持/反对边 + 可信度（见 05） |
| "反驳"谓词（9.2 推理型） | 取消——反对 = `支持 + 负 strength`（见 05） |
| DreamEngine (Order+Patrol) | 新维护引擎（标定+密度检测+归档）+ 探索者（找关联/问题，设计见 03，调度节奏待定见 06） |
| ReviewEngine | 验证者——处理 ReviewQueue，git 式暂存提交，拉开可信度差距（见 03/05） |
| Beacon 自动生成部分 | ReviewQueue 自动入队（Conflict/Dream/Manual 统一来源，见 05 6.0） |
| Beacon 工作端标记 | 保留（mark_for_review → ReviewQueue Source=Manual） |
| EvaluationEngine/EvaluationScores | 并入心智引擎，派生自断言（主体=用户时按断言性质驱动评价，见 06） |
| RelationClassificationCore | 不需要 |
| MemoryDecay/ChangePropagation | 不需要 |
| SleepTalkCore | 已删除（2026-08-24 死核清理：DreamEngine 链移除后类零实例化） |
| ExtractionEngine | 合并进心智引擎（伴随提取 + Session 善后两条链路） |

## 废弃清单

**推倒重来**的旧系统：旧记忆库（Memories/TempMemories/MemoryLinks/PersonaMemories）、做梦系统（DreamEngine/Order/Patrol）、复盘系统（ReviewEngine/EvaluationEngine）。遗留的 Review 自动触发半停机问题（DreamEngine 无定时调度）在重设计时一并解决。

**可信度模型落地后的新增废弃**：
- "反驳"谓词取消（反对 = `支持 + 负 strength`）
- 旧信标的自动生成部分取消（框架自动验证信号 = ReviewQueue 自动入队）
- 旧"矛盾检测 = LLM 关系分类顺带"取消（新检测 = 确定性排他属性检查，不靠 LLM）
- 旧"冲突直接触发 Review"取消（冲突组超预算 → 入 ReviewQueue，Review 消费队列而非直接消费冲突组）

## 待定与暂缓

**完整清单见 [06-pending-todos.md](06-pending-todos.md)**（待定 = 需决策未定；暂缓 = 明确首版不做）。

本文件保留与旧系统迁移直接相关的对照、废弃、路线。

## 实施路线（草稿）

> 按依赖顺序分阶段，每阶段可独立交付验证。详细实施计划待 writing-plans 流程产出。

**阶段 0 · 建库**：新增表（Claims/Journals/Keywords/FragmentKeywords/EmotionSnapshot/FrameworkEvents + ConflictGroups/ConflictGroupMembers + ReviewQueue）+ EmotionState.json，迁移配置
**阶段 1 · 情绪服务**：EmotionService（五轴 + 锁 + 实时 + 持久化 + 快照），IEmotionAccess SDK
**阶段 2 · Writer + 冲突 + 队列**：WriteBatch 原子批次、关键词关联、可信度锚点写入、ConflictService（排他检测/建组入组）、EvidenceEngine（noisy-OR + logit）、ReviewQueueService（入队/认领/恢复）、情绪转交
**阶段 3 · 产生端**：Express 伴随提取（单轮）+ wait journal 参数 + 框架事件启停
**阶段 4 · 复活善后**：监听 OnCompleted → 从存档重建 → 换记忆工具组 → 注入整理任务
**阶段 5 · 检索 + Review + 维护**：RetrieveService（可信度门控）、Review 验证流程、MaintenanceEngine、Dream 探索者
**阶段 6 · 清理**：废弃旧表/旧引擎，更新 WebUI，文档收尾

## 关键决策记录

- **矛盾不建表**：冲突组 + 可信度表达（见 05，取代旧"反驳"断言方案）
- **属性允许多值**：无唯一性约束，模型写时把握；**排他属性例外**（Keywords `IsExclusive=true`，同槽位触发冲突组）（见 01/05）
- **善后 = 复活**：不做拦截，end_session 成功即真结束，心智引擎复活（见 03）
- **Express 伴随提取单轮**：不开 session（见 03）
- **可信度是断言属性**：写入者定初值，Review 可改；推翻 9.2"无 confidence"（体系性修正，见 05）
- **强度与可信度正交**：`爱+0.9` 可低可信（见 05）
- **支持/反对边**：跨断言否定 = `支持 + 负 strength`，支持边自带可信度；无"反驳"谓词（见 05）
- **组内和 ≤1 恒定**：冲突各方瓜分信念预算，解决后也成立（见 05）
- **归一化 = Review 最后手段**：默认不自动，仅无实际证据时等比缩放（见 05）
- **跨等级碾压自动处理**：stated 直接压 inferred，不建工单（见 05 4.6）
- **ReviewQueue 统一工作源**：冲突组超预算、Dream 发现、手动 mark 都入队；冲突组退化为纯数值约束（见 05 6.0）
- **git 式提交**：Review 改动进暂存区（持久化），review_preview 预览影响（dry-run），review_commit 显式提交（原子事务 + 重算 + 校验）（见 05 6.0）
- **Dream 轻量**：无 session 循环；产物 = 推断断言（IsInference=true）+ ReviewQueue 入队；经历主要由 Review 写，Dream 结束框架生成日志式 L3
