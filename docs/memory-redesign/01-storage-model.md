# 存储模型

> 记忆重构计划 · 文件 01
> 状态：**定稿** — 2026-08-02

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
├── strength                   -- 可选，-1~1（态度方向强度）
├── valid_from / valid_until
├── recorded_at
├── OccurredAt                 -- 经历发生时间（补充）
├── Status: Raw|Tagged|Processed|Archived
└── Keywords  → FragmentKeywords
```

- 实体**不建独立表**：subject/predicate/object 的 `index` 存表面词字符串，实体身份 = 槽位组合，不做实体归并
- 无 polarity / confidence 字段（可靠性靠来源链派生）

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

## 矛盾的表达（不建 Conflicts 表）

矛盾检测的产物本身是一条显式断言，无需单开表。Writer 检测到新断言 B 与已有断言 A（同 subject+predicate、object 不同）矛盾时，自动生成：

```
Claims: { subject:   {type: assertion, ref: 断言B},
          predicate: {type: relation, index: "反驳"},
          object:    {type: assertion, ref: 断言A} }
```

- "反驳"是断言文档 9.2 已定的推理型谓词，方向约定：新断言反驳旧断言，系统不仲裁谁对
- 矛盾本身可检索、可引用为 Evidence、可被后续断言覆盖（新信息=新增碎片），无需 status 字段
- 被反驳者不特殊标记，查询层印证度派生时自然降权（反驳链路本身就是证据链的一部分）

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
- 覆盖/同源/矛盾检测规则遵循该文档（不自动仲裁，同 utterance_id 防重复计分）
- 属性**允许多值、无唯一性约束**：同一事物可有多个相同属性值（如"颜色"白+黑），多条断言并存；单值/多值语义由模型写时把握，系统不强制（实体不合并已消除跨实体误并，语义矛盾靠查询层 + 反驳链路自然收敛）

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

- 关键词匹配 Claims / Journals
- Journal: 按时间排列，时间窗口补相邻碎片
- Claim: 按来源链派生的印证度降序 + 时间
- 混合结果，标注类型（Claim / Journal）
- 同 utterance_id 的多条 Claim 并排返回

模型自己从结果中感受模式和进展。模式 = 同一关键词在碎片中的密度 + 时间分布，是检索视图而非存储结构。
