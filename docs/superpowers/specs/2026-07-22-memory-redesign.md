# 记忆系统重设计 — Claims + Journals 统一模型

> **状态：设计中** — 2026-07-22
> **经历（Journal）+ 情绪模型 + 记忆产生端已定稿（2026-08-01）**，见"记录层级"、"情绪模型"、"产生端"章节

## 问题

当前记忆系统是纯"事实仓库"——碎片化第三人称命题，缺三个东西：

1. **"我"**：Lilara 作为观察者、参与者、运行者的主体视角
2. **模式**：跨记忆的规律感，"习惯/节奏/因果"
3. **计数/频次**：防重复机制压制了重复信号

能回答"关于 X 你知道什么"，回答不了"有什么规律"和"什么被反复强化过"。

## 核心决策

**推倒重来。** 旧记忆库（Memories/TempMemories/MemoryLinks/PersonaMemories）、做梦系统（DreamEngine/Order/Patrol）、复盘系统（ReviewEngine/EvaluationEngine）全部替换。

### 基本原则

- **两张表统一记忆** — 不区分临时库/永久库，不建网状关联；Claims（断言，三元组）+ Journals（经历，正文式）本就是两类，各用一张
- **单库模式** — 写入即正式条目，靠状态字段区分处理阶段，不走 Temp→Main 迁移
- **无关联图** — 关键词库承担关联，时间轴承担序列，不需要 MemoryLinks 边表
- **几乎不覆盖** — 只有同一来源明确更正自己时才覆盖；新信息=新增碎片，不是替换（详细规则遵循断言文档，不自动仲裁）
- **窄命题优先** — 不写"xxx最强"，拆成"用户声称xxx最强"+"xxx在A榜得分X"

## 存储表结构（定稿）

记忆侧拆**两张表**：Claims（断言，三元组）+ Journals（经历，正文式），本就是两类东西。共享 Keywords 关联。

### Claims（断言，三元组）

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

### Journals（经历，正文式）

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

### Keywords（合并开放属性名 + 关键词）

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

### 情绪

```sql
EmotionState.json              -- 当前五轴值（-1~+1），初始 0，原子写，重启加载

EmotionSnapshot                -- 主库表，每次情绪更新记录
├── Id
├── Excitement / Joy / Alertness / Satisfaction / Optimism: float
├── SourceRef?                 -- 触发来源（Journal/L1 引用）
└── CreatedAt
```

### 框架事件（L0/L1，不进 Claims/Journals）

```sql
FrameworkEvents
├── Id
├── Level: L0|L1
├── Type                       -- 事件类型（工具失败/超时/用户否定）
├── Detail: JSON               -- 结构化详情
├── EmotionSignals: JSON?      -- L1 可携带
└── Timestamp
```

### 矛盾的表达（不建 Conflicts 表）

矛盾检测的产物本身是一条显式断言，无需单开表。Writer 检测到新断言 B 与已有断言 A（同 subject+predicate、object 不同）矛盾时，自动生成：

```
Claims: { subject:   {type: assertion, ref: 断言B},
          predicate: {type: relation, index: "反驳"},
          object:    {type: assertion, ref: 断言A} }
```

- "反驳"是 9.2 已定的推理型谓词，方向约定：新断言反驳旧断言，系统不仲裁谁对
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

## 产生端：碎片从哪来

提取能力**全部收敛进记忆引擎**。两条链路共用同一套提取逻辑，只是触发时机不同。

### 双模式分工

| 模式 | 触发 | 产出 | 情绪 |
|---|---|---|---|
| **Express 伴随提取** | 消息攒批 + 空闲补一次 | 窄 Claim（用户说了X） | 不涉及 |
| **Express wait** | 模型主动调 wait 带 journal 参数 | L2 Journal | 实时 |
| **Session 运行期** | 暂停伴随提取；模型可随时调 journal | L2/L3 Journal | 实时 |
| **Session 善后** | end_session / compact 归档后旧 session 进入 finalizing 态接续整理 | Claim + L2/L3 Journal | 由记忆引擎统一写 |
| **框架事件** | 工具失败 / 超时 / 用户否定等 | L0/L1 + 情绪信号 | 实时 |

### Express 模式（伴随提取 + wait）

- **伴随提取**：一直跟随聊天记录做轻量提取，产窄 Claim（"用户说了X"），带消息引用作为 Evidence
- 价值密度低但成本也低（只有聊天记录），保持实时
- 主要用途：**沉默监听**——即使不说话，也在默默积累对频道用户的了解
- 触发节奏：消息攒批为主 + 空闲补一次，保证监听及时又不漏尾巴

### Session 模式（运行期暂停 + 善后接续）

- **运行期不做伴随提取**（暂停这条链路），模型可随时调 `journal` 记当下（L2）
- **善后 = 旧 session 自身的 finalizing 态**（不是另开整理会话）：

  ```
  end_session / compact 归档
    └─ 旧 session 不销毁，进入 finalizing（善后）态
         ├─ 对外：已结束 —— 释放频道占用、不回复用户、WebUI 显示"归档中"
         ├─ 对内：续命 —— 注入整理指令 + 记忆工具组
         │       历史、工具轨迹本来就在它的上下文里，模型直接整理
         └─ 跑多轮 → 达轮次上限 → 真正归档销毁
  ```

  - **上下文是真实连续的，不是搬运的**：单独开整理会话得把旧历史摘要/重放进去，接续则模型从头到尾都看得见——它自己调过什么工具、说过什么、经过什么，就是它自己的记忆，对应关系天然成立，无需 parent 链接维系
  - **compact 特例**：compact 本来就是"旧 session 退出 + 新 session 承接摘要"。现在两者并存各司其职——对话交给新的，记忆交给旧的：

    ```
    compact(summary)
      ├─ 新 session 承接摘要，继续对话（现有逻辑不变）
      └─ 旧 session 进入 finalizing，续命做善后 → 整理完归档
    ```

  - 产出：
    - Claim（用户陈述 + 验证结论）
    - Journal（L2 当时经历 + L3 事后反思）
  - Session 的工具调用结果是高价值记忆源，集中到善后一次性深度处理（而不是运行期轻量链路浪费）
  - 工具权限：finalizing 态临时注入记忆工具组（journal / remember / claim / archive），结束后回收
  - 需新增配置：**finalizing 轮次上限**（MaxFinalizeRounds，防无限整理，超限强制归档）

### wait 工具（兼顾 Express 与 Session）

- 模型调 wait 时可带简短 `journal` 参数，随手记当下经历 + 情绪变动
- 主体内容与情绪变动由**模型自己写完整**，框架只补元数据（Source / ChannelId / SessionId / 时间戳），不做情感推断

### 提取协议（ExtractService → Writer 的 JSON 契约）

提取器产出 **JSON**，Writer 直接解析落库。`wait` 工具的 `journal` 参数复用同一 schema——模型手写、提取器机器产，Writer 无差别解析。

**容器**（一次提取的完整产出）：

```json
{
  "claims":   [ ],
  "journals": [ ]
}
```

**Claim**（窄断言）：

```json
{
  "subject":   { "type": "entity|keyword|attribute|literal|assertion", "index": "咪咪" },
  "predicate": { "type": "relation|attribute", "index": "喜欢" },
  "object":    { "type": "entity|keyword|attribute|literal|assertion", "index": "猫粮" },
  "raw": "用户说咪咪喜欢猫粮",
  "source": { "who": "user", "ref": { "kind": "message", "id": 12345 } },
  "utterance_id": "u_001",
  "strength": 0.8,
  "valid_from": null,
  "valid_until": null,
  "occurred_at": "2026-08-01T12:00:00+08:00"
}
```

**Journal**（经历）：

```json
{
  "kind": "Experience|Reflection",
  "content": "今天用户问我……",
  "source": "Session|Framework|UserCommand|System",
  "channel_id": null,
  "session_id": 42,
  "emotion_signals": [ { "axis": "joy", "target": 0.4, "reason": "..." } ],
  "keywords": ["频道", "用户"],
  "occurred_at": "2026-08-01T12:00:00+08:00"
}
```

**Writer 解析契约**：

| 字段 | 落库动作 |
|---|---|
| Claim.keyword/attribute 槽位 | 匹配/新建 Keywords（Role 区分）→ FragmentKeywords |
| Claim.entity 槽位 | 只存 index 作显示/原文返回，不匹配不归并 |
| Claim.raw | 整句存储，召回直接返回 |
| Claim.source | Evidence 引用（消息/经历/事件） |
| Journal.keywords | 匹配/新建 Keywords → FragmentKeywords |
| Journal.emotion_signals | **事务内转交 EmotionService**（读最新→算→写→快照） |
| Journal.content | 正文存储，元数据（recorded_at 等）由 Writer 补全 |

### 框架事件

- 工具失败 / 超时 / 用户否定等 → 写 L0/L1 记录并提交情绪信号，实时应用
- 具体事件 → 目标值映射表待定
- **程序启动/停止（已定稿）**：落 L0 框架事件，纯机械日志，不带情绪信号：

  ```
  FrameworkEvents: { Level: L0, Type: "ProcessStarted" | "ProcessStopped",
                     Detail: { version, pid, reason?, uptime? }, Timestamp }
  ```

## 记忆引擎（整体设计）

记忆引擎持有情绪服务，是**唯一写入口 + 唯一情绪写入者**。提取、事件、模型工具调用产出的数据全部汇入 Writer 统一落库、统一应用情绪。

### 模块结构

```
MemoryEngine
├── Writer                 # 统一写入口（提取产物 + 模型 journal 都走这）
│   ├── ClaimWriter        # 槽位解析 + 关键词关联 + utterance_id + 同源/矛盾检测
│   ├── JournalWriter      # 补元数据（Source/锚点/时间戳），EmotionSignals 转交情绪服务
│   └── EventWriter        # L0/L1 落表 + 情绪信号转交（SubmitEvent 通道）
├── ExtractService         # 提取核心（两条链路共用一套逻辑，产出 JSON）
│   ├── ExpressExtractor   # 伴随提取：消息攒批 + 空闲补一次（沉默监听，单轮调用）
│   └── FinalizeInjector   # 善后注入：finalizing 态注入整理指令 + 记忆工具组（接续，非另开会话）
├── EmotionService         # 五轴状态 + 实时应用 + EmotionState.json 持久化 + 快照
├── EventSink              # 订阅总线 → 判定 L0/L1 + 情绪目标值映射（映射表待定）
├── RetrieveService        # 关键词 + 时间窗口检索，印证度按 utterance_id 去重派生
└── MaintenanceEngine      # 标定关键词 / 密度检测 / 归档（巡逻节奏待定）
```

### Writer 写协议

**Writer 是记忆引擎内部唯一写入口**。提取器、`wait`/`remember` 的 journal 参数产出的数据全部汇入 Writer，由它统一解析落库、统一应用情绪。**消费端（工具/插件）不得直接碰表**。

#### 统一入参（WriteBatch）

复用"提取协议"的容器结构，一次调用 = 一个原子批次：

```json
{
  "claims":   [ ],
  "journals": [ ]
}
```

| 来源 | 包装方式 |
|---|---|
| 提取器产出 | 容器原样传入 |
| wait/remember 的 journal | 包装成 `journals: [单个]` |
| 事件（L0/L1） | **不走此容器**，走独立 `SubmitEvent` 通道 |

#### 写入流程（原子批次）

```
WriteBatch 进入
1. 解析         JSON → 内部实体（Claim/Journal）
2. 关键词关联    keyword/attribute 槽位 + journal.keywords → 匹配/新建 Keywords → FragmentKeywords
                （entity 只存 index 不归并；literal 直存；assertion 引用断言 id）
3. 断言处理     同 utterance_id 分组（同源防重复计分）
               矛盾检测（同 subject+predicate 比对）→ 自动生成"反驳"断言挂起，不仲裁
               覆盖遵循断言文档（几乎不覆盖，同源明确更正才覆盖）
4. 情绪应用     收集全部 journals 的 emotion_signals → EmotionService.ApplyAsync
5. 落库         Claims / Journals / Keywords / FragmentKeywords / EmotionSnapshot 一次事务
6. 提交；失败回滚（EmotionState.json 事务成功后原子写）
```

**原子性的意义**：一次提取的多条断言共享 utterance_id，必须整体成功或整体回滚，否则孤儿断言破坏同源分组。

#### 情绪事务边界

- EmotionService 内部持锁，串行化所有 `ApplyAsync`（读最新 → 算 → 更新内存）
- **EmotionSnapshot 随 Writer 事务落库**（同连接同事务，数据库完全一致）
- **EmotionState.json 事务成功后原子写**（文件不参与事务，失败可重试；重启加载时 EmotionState.json 优先，缺失/损坏则从 EmotionSnapshot 恢复最新）
- 独立 `SubmitSignalAsync` 调用（如未来事件源直接提交）各自开事务，同样实时

#### 事件接入预留

- Writer 提供 `SubmitEvent(level, type, detail, emotionSignals?)` 通道，EventSink 后续接入
- 当前仅落地程序启停（L0、无情绪），其余事件映射表待定——**接口先留好，实现后续填**

#### 消费端约束

- 插件提交情绪只能经 `IEmotionAccess`，写记忆只能经 Writer 暴露的工具（journal/remember）
- 不暴露直接改绝对值、删表、删快照

## 待定

**经历 + 情绪部分（2026-08-01 定稿后剩余）：**
- 各轴 rate 初值与热容 JSON schema（当前值存 EmotionState.json 已定，rate 数值待定）
- 各轴框架乘数具体数值（maxInfluence 系数）
- 自动事件 → 目标值映射表（工具失败 / 用户否定 / 任务完成等分别推哪些轴、什么值）
- `journal` / `remember` 工具签名（journal 结构已定，参数 schema 待定）
- 记忆引擎：模块结构 + Writer 写协议 + 善后接续已定（见"记忆引擎"/"产生端"章节），待定：ExtractService 两链路的具体实现、EventSink 事件映射表
- finalizing 态的配置（MaxFinalizeRounds 轮次上限等，具体字段待定）
- 情绪快照历史（EmotionSnapshot 表已定，具体字段/触发细节待定）

**记忆系统其余部分：**
- 维护引擎的巡逻节奏和具体流程（标定关键词、密度检测、归档）
- 印证度（Corroboration）的派生计算公式（数独立来源，按 utterance_id 去重）
- 念头系统（产生/竞争/兑现）
- `expect` 参数解析 → TimerEngine 注册 → 到期唤醒的具体链路
- 信任/评价系统的去向（保留？简化？合并？）
- Express 伴随提取的具体触发参数（攒批 N 条 / 空闲 N 分钟）
- Session 工具轨迹的读取与记忆判定策略（标记优先 vs 全量，token 成本控制）
