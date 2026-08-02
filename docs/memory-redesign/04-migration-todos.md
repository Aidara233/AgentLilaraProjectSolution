# 迁移与待定

> 记忆重构计划 · 文件 04
> 状态：**待定为主** — 2026-08-02

## 与旧系统的对照

| 旧组件 | 新设计 |
|---|---|
| Memories 表 | Claims 表（三元组断言） |
| TempMemories 表 | 无（单库模式） |
| MemoryLinks 表 | 无（关键词承担关联） |
| PersonaMemories 表 | 合并到 Claims/Journals 或独立表 |
| MemoryService/RecallAsync | 新检索服务（关键词+时间窗口） |
| MemoryExtractionCore | 新提取逻辑 |
| DreamEngine (Order+Patrol) | 新维护引擎（标定+归档+密度检测） |
| ReviewEngine | 合并到维护引擎或简化 |
| EvaluationEngine/EvaluationScores | 待定 |
| Plugin.MemoryTools | 新工具集 |
| Plugin.ReviewTools | 合并或重写 |
| RelationClassificationCore | 不需要 |
| MemoryDecay/ChangePropagation | 不需要 |
| SleepTalkCore | 保留（与记忆库无关） |
| ExtractionEngine | 合并进记忆引擎（伴随提取 + Session 善后两条链路） |

## 废弃清单

**推倒重来**的旧系统：旧记忆库（Memories/TempMemories/MemoryLinks/PersonaMemories）、做梦系统（DreamEngine/Order/Patrol）、复盘系统（ReviewEngine/EvaluationEngine）。遗留的 Review 自动触发半停机问题（DreamEngine 无定时调度）在重设计时一并解决。

## 待定

### 经历 + 情绪部分

- 各轴 rate 初值与热容 JSON schema（当前值存 EmotionState.json 已定，rate 数值待定）
- 各轴框架乘数具体数值（maxInfluence 系数）
- 自动事件 → 目标值映射表（工具失败 / 用户否定 / 任务完成等分别推哪些轴、什么值）
- `journal` / `remember` 工具签名（journal 结构已定，参数 schema 待定）
- 记忆引擎：模块结构 + Writer 写协议 + 善后复活已定（见 03），待定：ExtractService 两链路的具体实现、EventSink 事件映射表
- 复活整理的配置（MaxFinalizeRounds 轮次上限、从存档重建时如何注入整理任务等，具体字段待定）
- 情绪快照历史（EmotionSnapshot 表已定，具体字段/触发细节待定）

### 记忆系统其余部分

- 维护引擎的巡逻节奏和具体流程（标定关键词、密度检测、归档）
- 印证度（Corroboration）的派生计算公式（数独立来源，按 utterance_id 去重）
- 念头系统（产生/竞争/兑现）
- `expect` 参数解析 → TimerEngine 注册 → 到期唤醒的具体链路
- 信任/评价系统的去向（保留？简化？合并？）
- Express 伴随提取的具体触发参数（攒批 N 条 / 空闲 N 分钟）
- Session 工具轨迹的读取与记忆判定策略（标记优先 vs 全量，token 成本控制）

## 实施路线（草稿）

> 按依赖顺序分阶段，每阶段可独立交付验证。详细实施计划待 writing-plans 流程产出。

**阶段 0 · 建库**：新增 6 表 + 1 JSON（Claims/Journals/Keywords/FragmentKeywords/EmotionSnapshot/FrameworkEvents + EmotionState.json），迁移配置
**阶段 1 · 情绪服务**：EmotionService（五轴 + 锁 + 实时 + 持久化 + 快照），IEmotionAccess SDK
**阶段 2 · Writer**：WriteBatch 原子批次、关键词关联、矛盾检测、情绪转交
**阶段 3 · 产生端**：Express 伴随提取（单轮）+ wait journal 参数 + 框架事件启停
**阶段 4 · 复活善后**：监听 OnCompleted → 从存档重建 → 换记忆工具组 → 注入整理任务
**阶段 5 · 检索 + 维护**：RetrieveService、MaintenanceEngine
**阶段 6 · 清理**：废弃旧表/旧引擎，更新 WebUI，文档收尾

## 关键决策记录

- **矛盾不建表**：用"反驳"断言表达（见 01 存储模型）
- **属性允许多值**：无唯一性约束，模型写时把握（见 01）
- **善后 = 复活**：不做拦截，end_session 成功即真结束，记忆引擎复活（见 03）
- **Express 伴随提取单轮**：不开 session（见 03）
