# 记忆系统重设计 — 碎片统一模型

> **状态：设计中** — 2026-07-22
> **经历（Journal）+ 情绪模型部分已定稿（2026-08-01）**，见"记录层级"与"情绪模型"章节

## 问题

当前记忆系统是纯"事实仓库"——碎片化第三人称命题，缺三个东西：

1. **"我"**：Lilara 作为观察者、参与者、运行者的主体视角
2. **模式**：跨记忆的规律感，"习惯/节奏/因果"
3. **计数/频次**：防重复机制压制了重复信号

能回答"关于 X 你知道什么"，回答不了"有什么规律"和"什么被反复强化过"。

## 核心决策

**推倒重来。** 旧记忆库（Memories/TempMemories/MemoryLinks/PersonaMemories）、做梦系统（DreamEngine/Order/Patrol）、复盘系统（ReviewEngine/EvaluationEngine）全部替换。

### 基本原则

- **一张表统一一切** — 不区分临时库/永久库，不区分知识/信念，不建网状关联
- **单库模式** — 写入即正式条目，靠状态字段区分处理阶段，不走 Temp→Main 迁移
- **无关联图** — 关键词库承担关联，时间轴承担序列，不需要 MemoryLinks 边表
- **几乎不覆盖** — 只有同一来源明确更正自己时才覆盖；新信息=新增碎片，不是替换
- **窄命题优先** — 不写"xxx最强"，拆成"用户声称xxx最强"+"xxx在A榜得分X"

## Fragment 实体

```
Fragments（一张表统一所有碎片）
├── Id
├── Type: Journal | Claim
│
├── Content: string              — 碎片内容
├── ClaimKey: string?            — 归一化断言键，Journal=null
│   格式: {来源主体}/{维度}:{值}
│   维度词从关键词库取标准化形式，保证表述统一
│   示例: "person:小明/preference:饮料" → "咖啡"
│         "@用户/声称/capability:代码" → "xxx排名第一"
│
├── Corroboration: float         — 印证度 0~1
│   0.1 = 单一随口陈述
│   0.5 = 多个独立来源指向同一方向
│   0.9 = 实测/文档/多方交叉验证
│   Journal 不需要此字段
│
├── Evidence: [ {type, ref, note} ]  — 证据链，多项
│   type: message / url / fragment / document / benchmark
│   ref:  具体引用（msg:12345 / https://... / frag:67）
│   note: 简述
│
├── Status: Raw | Tagged | Processed | Archived
├── RevisionCount: int           — 被覆盖次数（极罕见）
├── OccurredAt: DateTime
│
└── Keywords: [KeywordId]        — 通过 FragmentKeywords 关联

Journal 专有字段（Type=Journal 时）:
├── JournalKind: Experience | Reflection   — L2 / L3
├── Source: Framework / Session / UserCommand / System  — 产生者
├── ChannelId? / SessionId?               — 上下文锚点（追溯用）
├── EmotionSignals: [ {Axis, Target, Reason} ]  — JSON 列，连续目标值
│   Axis: Excitement / Joy / Alertness / Satisfaction / Optimism
│   Target: -1.0 ~ +1.0（吸引子目标值，非增量）
│   Reason: 简短理由
└── ExpectRaw: string?                    — 可选预期文本

情绪状态存储（独立于 Fragments 表）:
├── EmotionState.json  — 当前五轴值（-1~+1），初始 0.0，实时原子写，重启加载
└── EmotionSnapshot    — 历史变动记录（每次信号应用后），保留与否待定
```

## 类型行为

### Journal（经历，不可变）

- 第一人称叙事，Lilara 为主体
- 写入即历史，不可覆盖不可修正
- 不参与 ClaimKey/Corroboration/覆盖逻辑（可被引用为 Evidence 对象）
- 检索靠关键词 + 时间轴（时间窗口在检索时设定，不预存）
- 按 JournalKind 区分 Experience(L2) / Reflection(L3)
- 携带 EmotionSignals（连续目标值），写入时**实时**应用
- 用户可删：仅管理页面手动删除，无运行时删除工具

### Claim（断言，可修正）

- 所有关于世界的陈述，窄命题形式
- ClaimKey = `{来源主体}/{维度}:{值}`，维度词经关键词库归一化
- **覆盖规则**：仅当新 ClaimKey == 旧 ClaimKey **且** 新碎片明确为同一来源的更正时才覆盖
  - 不同来源不覆盖（`@用户/说/xx` 和 `@benchmark/显示/xx` 是不同的 key，主体不同）
  - 同一来源不同时间的观点变迁也不覆盖——保留演变
  - 覆盖是极罕见的例外，不是核心机制
- Corroboration 基于证据数量和质量可自动推算

## 记录层级（L0-L3）

经历与情绪来源分四个等级，决定"是否进记忆库"与"能否影响情绪"：

| 层级 | 名称 | 产生者 | 进 Fragments | 影响情绪 |
|---|---|---|---|---|
| L0 | FrameworkEvent | 框架 | 否 | 可 |
| L1 | ExperienceRecord | 框架/Session | 否 | 可 |
| L2 | Journal-Experience | Session/框架 | 是 | 可 |
| L3 | Journal-Reflection | Session | 是 | 可 |

- **L0**：纯机械运行日志（工具失败/超时/重试），结构化，不污染记忆库，不设保留期
- **L1**：重要框架标记（连续失败/用户否定等），可携带情绪信号，模型可引用为 Evidence 但**不可升级**
- **L2/L3**：进入 Fragments，Type=Journal，JournalKind=Experience/Reflection
- **升级必须"认领"**：L1 只能被 Session 明确认领升级为 L2，框架不能自动升级
- 模型总结时可提及 L0/L1 小节点作为 Evidence，但小节点身份不变

## 关键词库（共享基础设施）

```
Keywords
├── Id
├── Name: string                 — 标准化名称
├── Aliases: [string]            — 别名列表（喜欢/爱喝/常喝 → 同一关键词）
├── Category: Person | Topic | Behavior | Place | Event
├── UseCount: int
└── CreatedAt / LastUsedAt

FragmentKeywords（关联表）
├── FragmentId
└── KeywordId
```

关键词标定由维护期巡逻完成，不要求模型在写入时手动填。

## 检索

无 Collection、无组、无叙事线、无关联图。检索靠关键词 + 时间窗口。

- 关键词匹配碎片
- Journal: 按时间排列，时间窗口补相邻碎片
- Claim: 按 Corroboration 降序 + 时间
- 混合结果，标注类型
- 同 ClaimKey 的多条碎片并排返回

模型自己从结果中感受模式和进展。模式 = 同一关键词在碎片中的密度 + 时间分布，是检索视图而非存储结构。

## 情绪模型

### 定位（双层状态）

- **内部状态**：情绪是 Lilara 的真实内部状态，核心效果是**框架调控**——调节冲动值、检索量、注意力等内部决策
- **用户感知**：通过 Prompt 注入和表达风格，让用户明显感知情绪
- 模型不能直接修改情绪绝对值，不能作为状态的唯一裁判；情绪变化统一由 EmotionService 计算

### 五根轴（-1.0 ~ +1.0，连续值）

| 轴 | 正极 | 负极 | 比热容 | 衡量 |
|---|---|---|---|---|
| 兴奋 | 有劲、来劲 | 无聊、没意思 | 最低 | 精力水平 |
| 喜悦 | 开心、心情好 | 低落、沮丧 | 中低 | 即时心情 |
| 敏觉 | 敏锐、能接住 | 冷淡→麻木 | 中高 | 感知开放度 |
| 满足 | 够了、圆满 | 还差点→空虚 | 中高 | 现状评价 |
| 积极 | 有希望、看好 | 不看好→悲观 | 最高 | 未来预期 |

### 数值语义

- 内部使用**连续值** `[-1.0, +1.0]`
- 事件提交的是某根轴的**目标值**（吸引子），不是直接增量
- 五级 `++ / + / 0 / - / --` 保留为人类与 Prompt 的快捷语法（映射 1.0 / 0.4 / 0 / -0.4 / -1.0），**不是内部精度限制**；需要时可提交任意连续值（如 `-0.12`、`+0.68`）
- 调用方不能直接写当前绝对值

### 运动公式

```
new = current + (target - current) × rate
```

- rate 由该轴的热容决定：低热容 rate 高（兴奋一推就动），高热容 rate 低（积极稳得住）
- 无衰减——没有新信号就不动
- 天然边界阻力：越近目标 delta 越小，稳定趋近而不触碰极值

### 更新机制（实时 + 全局唯一）

- **实时应用**：任何来源提交 EmotionSignal → 立即同步计算新值 + 更新内存当前值 + 持久化
- **全局唯一 EmotionService**：唯一写入入口，事务内"读最新值 → 计算 → 写"；锁保证并发一致，避免多个来源基于同一旧值覆盖彼此
- 由（正在重新设计的）**记忆引擎**持有 EmotionService，兼顾情绪系统
- 信号实时应用（不等待升级）；L0/L1/L2/L3 都可携带 EmotionSignals
- 初始值 0.0，当前值持久化到 `EmotionState.json`（原子写，重启加载）

### 框架调控（乘积映射）

- 线性乘数 + `maxInfluence` 上界（每个参数一个最大影响系数，防极端值失控），JSON 可调
- **只在群聊生效**（私聊绝对触发，无需情绪影响），不做分场景覆盖

| 轴 | 框架行为 | 示例公式 |
|---|---|---|
| 兴奋 | 冲动值速率 + 念头频率 + 念头视野 | `impulseRate = base × (1 + excitement × 0.6)` |
| 喜悦 | review 偏置 + 冲突消解倾向 | `reviewBias = base × (1 + joy × 0.4)` |
| 敏觉 | 噪声过滤门槛 + 上下文拉取量 + 提取强度 | `pullSize = base × (1 + alertness × 0.5)` |
| 满足 | 资源索取倾向 + 自我评价严格度 | `strictness = base × (1 + satisfaction × 0.3)` |
| 积极 | 念头色彩 + 持久/放弃门槛 + 子agent障碍策略 | `persistThreshold = base × (1 + optimism × 0.4)` |

### Prompt 注入

- 以**状态描述为主**：描述当前情绪状态 + 修正提示，少数情况才改动系统提示词
- 注入情绪状态词和引导语，模型据此调整输出风格
- 模型感知的是状态描述，不是五轴原始数值

### SDK 边界（插件）

- **核心不可替换**：唯一写入者、运动公式、热容、乘数映射、Prompt 生成
- `IEmotionAccess`：`GetCurrentAsync()` / `SubmitSignalAsync(EmotionSignal)` / `GetRecentSnapshotAsync()`
- **不暴露**：直接改绝对值、删快照、改热容、改乘数

## 与旧系统的对照

| 旧组件 | 新设计 |
|---|---|
| Memories 表 | Fragments (Type=Claim) |
| TempMemories 表 | 无（单库模式） |
| MemoryLinks 表 | 无（关键词承担关联） |
| PersonaMemories 表 | 合并到 Fragments 或独立表 |
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
| ExtractionEngine | 降为轻量批量提取（攒批LLM扫描），仅出窄Claim |

## 产生端：碎片从哪来

### 三入口

```
Express 消息 → 提取引擎（轻量批量 LLM 扫描）
  ├── 输入：聊天记录，攒够一批（N条/N分钟）触发
  ├── 产出：窄 Claim，"用户说了X"
  ├── 带消息引用作为 Evidence，Corroboration ≈ 0.2
  ├── 不做：推断/归纳/写 Journal
  └── 重量：轻量 LLM 调用，不走 CoreBase

Session 关闭流程 → 模型自己整理
  ├── end_session 后，注入 prompt："列出本次会话产生的所有新知识/验证结果/更正"
  ├── 产出：Claim（综合判断、验证结论、更正旧值）+ Journal
  ├── 可覆盖 Express 提取的低 Corroboration 同名 Claim
  └── 这是 Claim 的主要高质量来源

运行时 → 模型主动调用
  ├── journal 工具 → Journal（经验、印象、模式观察），任何时候
  └── remember 工具 → Claim（用户明确指令"记住这个"），即时写入
```

### 职责边界

| 入口 | 产出 | 触发 | 重量 |
|---|---|---|---|
| Express 提取引擎 | Claim（窄，低印证度） | 消息攒批 | 轻量 LLM |
| Session 关闭流程 | Claim + Journal（高质量） | Session 结束 | 模型整理 |
| `journal` 工具 | Journal | 模型自主 | 轻 |
| `remember` 工具 | Claim | 用户指令 | 轻 |

不再需要独立的 `ChannelExtractionWorker` + `MemoryExtractionCore`（7种类型+direction+about）。
提取引擎职责缩到一句话：从聊天记录里把**明确陈述**抽成窄 Claim，别的什么都不管。

### 情绪/经历的自动触发点

- Express 每次 `wait`
- Session 每次 `end_session`
- 框架事件（工具失败 / 超时 / 用户否定等，写 L0/L1 记录并提交情绪信号）
- 具体事件 → 目标值映射表待定

## 待定

**经历 + 情绪部分（2026-08-01 定稿后剩余）：**
- 各轴 rate 初值与热容 JSON schema（当前值存 EmotionState.json 已定，rate 数值待定）
- 各轴框架乘数具体数值（maxInfluence 系数）
- 自动事件 → 目标值映射表（工具失败 / 用户否定 / 任务完成等分别推哪些轴、什么值）
- `journal` / `remember` 工具签名
- Session 关闭整理流程细节
- 情绪快照历史（EmotionSnapshot 表）保留与否
- 记忆引擎具体设计（引擎跟着经历/情绪设计走）

**记忆系统其余部分：**
- 维护引擎的巡逻节奏和具体流程（标定关键词、密度检测、归档）
- Corroboration 的自动推算公式（证据数量+类型→分数映射）
- 念头系统（产生/竞争/兑现）
- `expect` 参数解析 → TimerEngine 注册 → 到期唤醒的具体链路
- 信任/评价系统的去向（保留？简化？合并？）
- 数据表最终结构（Fragments/Keywords/情绪配置JSON）
- Session 关闭流程标准化（end_session 后的善后步骤需要规范化到哪个文件）
- Express 提取引擎的攒批参数（N条或N分钟触发）
