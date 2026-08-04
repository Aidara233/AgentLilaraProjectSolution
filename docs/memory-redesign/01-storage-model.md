# 存储模型

> 记忆重构计划 · 文件 01
> 状态：**设计定稿（未实现）** — 2026-08-02（可信度/冲突模型于 08-02 更新，见 05）。当前代码仍是旧记忆库（无 Claims 等表），本存储模型尚未落库。
> 可信度与冲突的完整模型见 [05-credibility-conflict-model.md](05-credibility-conflict-model.md)。

记忆侧拆**两张表**：Claims（断言，三元组）+ Journals（经历，正文式），本就是两类东西。共享 Keywords 关联。

## Claims（断言，三元组）

遵循《断言存储系统设计决定.md》：

```sql
Claims
├── Id
├── subject   { type, index, ref? }   -- 槽位 JSON
├── predicate { type, index, ref? }
├── object    { type, index, ref? }
├── raw                        -- 拼好的整句原文（召回时直接返回）
├── source                     -- 谁说的 + 哪条消息/经历（承担引用职责）
├── utterance_id               -- 同源分组（一句话拆多条断言的共同出身）
├── strength                   -- 可选，-1~1（态度方向强度，程度值，见 05）
├── Credibility                -- 可信度锚点（写入者定初值，Review 可改，见 05）
├── IsInference                -- 推断标记（跨等级碾压 + 召回"推断"标签）
├── valid_from / valid_until
├── recorded_at
├── OccurredAt                 -- 经历发生时间（补充）
├── Status: Raw|Tagged|Processed|Archived
└── Keywords  → FragmentKeywords
```

- 实体**不建独立表**：subject/predicate/object 的 `index` 存表面词字符串，实体身份 = 槽位组合，不做实体归并
- **无 polarity / confidence 字段**（可靠性靠来源链派生），但有 `Credibility`（可信度锚点）与 `IsInference`（推断标记）——见 [05-credibility-conflict-model.md](05-credibility-conflict-model.md)，可信度是对 9.2 的体系性修正

## Journals（经历，正文式）

```sql
Journals
├── Id
├── Content                     -- 第一人称正文
├── JournalKind: Experience|Reflection   -- L2/L3
├── Source: Framework|Session|UserCommand|System
├── ChannelId? / SessionId?     -- 上下文锚点
├── EmotionSignals: JSON        -- [{Axis, Target, Reason}]
├── ExpectRaw?
├── OccurredAt / CreatedAt
├── Status: Raw|Tagged|Processed|Archived
└── Keywords  → FragmentKeywords
```

## Keywords（合并开放属性名 + 关键词）

```sql
Keywords
├── Id
├── Name
├── Aliases: JSON
├── Role: attribute | keyword    -- 开放属性名（断言侧） vs 普通关键词
├── Category?                    -- Person/Topic/Behavior/Place/Event（keyword 时用）
├── IsExclusive?                 -- 排他属性标记（属性关键词用，驱动冲突检测，见 05）
└── UseCount / CreatedAt / LastUsedAt

FragmentKeywords（共用关联表）
├── OwnerType: Claim|Journal     -- 关联对象类型
├── OwnerId
└── KeywordId
```

## 情绪

```sql
EmotionState.json              -- 当前五轴值（-1~+1），初始 0，原子写，重启加载

EmotionSnapshot                -- 主库表，每次情绪更新记录
├── Id
├── Excitement / Joy / Alertness / Satisfaction / Optimism: float
├── SourceRef?                 -- 触发来源（Journal/L1 引用）
└── CreatedAt
```

## 框架事件（L0/L1，不进 Claims/Journals）

```sql
FrameworkEvents
├── Id
├── Level: L0|L1
├── Type                       -- 事件类型（工具失败/超时/用户否定）
├── Detail: JSON               -- 结构化详情
├── EmotionSignals: JSON?      -- L1 可携带
└── Timestamp
```

## 矛盾的表达（支持/反对边 + 冲突组，见 05）

**跨断言否定不建"反驳"谓词**——统一用 `支持` 谓词 + 负 strength 表达（反对 = 支持 + 负强度）。"断言 S 支持/反对 A"本身是一条断言，subject/object 均为 assertion 类型，携带自己的可信度（万一这俩其实不冲突，支持边可信度低 → 搬运质量缩水，自愈）。

结构性冲突（同排他属性槽位取值不同）**单独建库**——冲突组：

```sql
ConflictGroups
├── Id
├── SubjectKey / PredicateKey     -- 冲突槽位（subject + 排他属性名）
├── Reason                         -- 检测原因
├── DetectedAt / ResolvedAt? / ResolvedBy?
└── Members → ConflictGroupMembers

ConflictGroupMembers
├── GroupId
├── ClaimId
└── JoinAt
```

- **无 status 字段**——状态全由数值派生（组内 `c_actual` 和 >1 = 需 Review）
- **两个冲突断言就建组**；后来的冲突断言加入现有组
- 触发：写入新断言与已有断言同（subject + 排他属性）槽位取值不同，排他属性 = Keywords `IsExclusive=true`
- **组内约束：成员 `c_actual` 之和 ≤ 1，恒定成立（解决后也成立）**
- **触发 Review：组内和 > 1**

可信度（`Credibility`）语义、程度值（`strength`）、支持/反对边的证据聚合公式、Review 验证流程，完整见 [05-credibility-conflict-model.md](05-credibility-conflict-model.md)。

Journal 的 Evidence 可引用 FrameworkEvent / Claims / Journals / 消息 Id。

## 类型行为

### Journal（经历，不可变）

- 第一人称叙事，Lilara 为主体
- 写入即历史，不可覆盖不可修正
- 不参与 Claims 的覆盖/同源逻辑（可被引用为 Evidence 对象）
- 检索靠关键词 + 时间轴（时间窗口在检索时设定，不预存）
- 按 JournalKind 区分 Experience(L2) / Reflection(L3)
- 携带 EmotionSignals（连续目标值），写入时**实时**应用
- 用户可删：仅管理页面手动删除，无运行时删除工具

### Claim（断言，遵循断言文档）

- 三元组结构 `subject/predicate/object`，槽位 `{type, index}`，index 存表面词
- 遵循《断言存储系统设计决定.md》：无 polarity/confidence，否定靠 strength 负数，可靠性靠来源链派生
- **覆盖/同源/矛盾检测规则以 05 为准**（可信度 + 冲突组取代旧"反驳"方案，不自动仲裁不变）
- 属性**允许多值、无唯一性约束**：同一事物可有多个相同属性值（如"颜色"白+黑），多条断言并存；单值/多值语义由模型写时把握，系统不强制（实体不合并已消除跨实体误并）。**排他属性**（Keywords `IsExclusive=true`）例外——同槽位取值不同触发冲突组（见 05）

## 记录层级（L0-L3）

经历与情绪来源分四个等级，决定"是否进记忆库"与"能否影响情绪"：

| 层级 | 名称 | 产生者 | 存储位置 | 影响情绪 |
|---|---|---|---|---|
| L0 | FrameworkEvent | 框架 | FrameworkEvents 表 | 可 |
| L1 | ExperienceRecord | 框架/Session | FrameworkEvents 表 | 可 |
| L2 | Journal-Experience | Session/框架 | Journals 表 | 可 |
| L3 | Journal-Reflection | Session | Journals 表 | 可 |

- **L0**：纯机械运行日志（工具失败/超时/重试），结构化，不污染记忆库，不设保留期
- **L1**：重要框架标记（连续失败/用户否定等），可携带情绪信号，模型可引用为 Evidence 但**不可升级**
- **L2/L3**：进入 Journals 表，JournalKind=Experience/Reflection
- **升级必须"认领"**：L1 只能被 Session 明确认领升级为 L2，框架不能自动升级
- 模型总结时可提及 L0/L1 小节点作为 Evidence，但小节点身份不变

## 检索

无 Collection、无组、无叙事线、无关联图。检索靠关键词 + 时间窗口。

> 注：支持/反对边（断言→断言，见 05）是断言间关系，不是"记忆内容图"；节点 = 关键词库/实体/用户，内容全承载在断言边上，图检索由 RetrieveService 做关键词 + 时间窗口，不做多跳图遍历。

### 排序规则（综合匹配度 + 可信度）

召回排序按两级，**命中关键词数量是绝对优先级**：

1. **命中关键词数量降序**（绝对优先级）——匹配度是硬标准，命中多的排前面
2. **运算可信度 `c_actual` 降序**（次优先级）——同命中数内，可信度高的排前面

### 子证据返回

- 同时返回**二跳以内的子证据**（支持/反对边，二跳内）
- 总计最大 **N 条（可配置）**，N 为召回总数上限

### 召回其余规则

- 关键词匹配 Claims / Journals
- Journal: 按时间排列，时间窗口补相邻碎片
- Claim: 按上述排序规则
- 混合结果，标注类型（Claim / Journal）
- 同 utterance_id 的多条 Claim 并排返回

> **检索中立**：模型主动查时不做频道奖励，保持公平；仅当检索条件显式标注"仅限该频道"才过滤（注入可有频道倾向，检索不——见 03"心智插件"）。

模型自己从结果中感受模式和进展。模式 = 同一关键词在碎片中的密度 + 时间分布，是检索视图而非存储结构。
