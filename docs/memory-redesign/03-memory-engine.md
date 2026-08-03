# 心智引擎整体规划

> 记忆重构计划 · 文件 03
> 状态：**骨架定稿** — 2026-08-02（Writer 协议、产生端、复活善后已定；EventSink 映射等实现细节待定）

## 定位：前额叶，不是海马体

**MindEngine（心智引擎）**不是"存记忆的地方"——它是统合记忆、情绪、评估、念头、写闸门的中枢，类似大脑的**前额叶**：

- **记忆**（Claims/Journals 存储、检索、维护）——"我经历过什么"（存储侧）
- **情绪**（五轴状态 + 实时应用）——"我现在感觉如何"（情绪侧）
- **可信度仲裁**（Review 验证、Dream 探索、冲突解决）——评估信息可信与否、抑制错误认知（认知抑制/错误监控）
- **念头系统**（02 已列为调控目标）——生成、筛选、放弃念头（工作记忆 + 决策）
- **唯一写入口**（Writer）——统一决策什么信息进入长期记忆（记忆巩固的调控闸门）

记忆只是其中一块，情绪、评估、念头、写闸门都是平级模块。所以它叫**心智引擎**而非"记忆引擎"——叫后者会把它降格成海马体，丢掉前额叶那部分职能。

心智引擎持有情绪服务，是**唯一写入口 + 唯一情绪写入者**。提取、事件、模型工具调用产出的数据全部汇入 Writer 统一落库、统一应用情绪。消费端（工具/插件）不得直接碰表。

## 模块结构

```
MindEngine
├── Writer                 # 统一写入口（提取产物 + 模型 journal 都走这）
│   ├── ClaimWriter        # 槽位解析 + 关键词关联 + utterance_id + 同源 + 可信度锚点写入
│   ├── JournalWriter      # 补元数据（Source/锚点/时间戳），EmotionSignals 转交情绪服务
│   └── EventWriter        # L0/L1 落表 + 情绪信号转交（SubmitEvent 通道）
├── ExtractService         # 提取核心（两条链路共用一套逻辑，产出 JSON）
│   ├── ExpressExtractor   # 伴随提取：消息攒批 + 空闲补一次（沉默监听，单轮调用）
│   └── Resurrector        # 善后复活：监听 OnCompleted → 从存档重建 session → 换记忆工具组 → 注入整理任务
├── EvidenceEngine         # 证据聚合：noisy-OR + logit/sigmoid，算实际可信度 c_actual（见 05）
├── ConflictService        # 冲突检测 + 建组/入组 + 组内和约束（见 05）
├── ReviewQueueService     # 待验证队列：入队（Conflict/Dream/Manual）+ 认领 + 状态 + 崩溃恢复（见 05 6.0）
├── ReviewStage            # Review 暂存区：git 式暂存 + preview 预览 + commit 提交（见 05 6.0）
├── EmotionService         # 五轴状态 + 实时应用 + EmotionState.json 持久化 + 快照
├── EventSink              # 订阅总线 → 判定 L0/L1 + 情绪目标值映射（映射表待定）
├── RetrieveService        # 关键词 + 时间窗口检索，印证度按 utterance_id 去重派生
├── DreamExplorer          # 轻量探索者：无 session，切片驱动，建推断断言 + 入队移交（见下）
└── MaintenanceEngine      # 标定关键词 / 密度检测 / 归档 + 巡逻触发 Dream（节奏待定）
```

## Writer 写协议

**Writer 是心智引擎内部唯一写入口**。提取器、`wait`/`remember` 的 journal 参数产出的数据全部汇入 Writer，由它统一解析落库、统一应用情绪。

### 统一入参（WriteBatch）

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

### 写入流程（原子批次）

```
WriteBatch 进入
1. 解析         JSON → 内部实体（Claim/Journal）
2. 关键词关联    keyword/attribute 槽位 + journal.keywords → 匹配/新建 Keywords → FragmentKeywords
                （entity 只存 index 不归并；literal 直存；assertion 引用断言 id）
3. 断言处理     同 utterance_id 分组（同源防重复计分）
               写入可信度锚点（写入者定初值：stated≈1.0 / inferred=推断度，见 05）
4. 冲突检测     同 subject+排他属性槽位取值不同 → ConflictService 建组/入组（见 05）
               支持/反对边（assertion 槽位）→ 收集证据边
5. 情绪应用     收集全部 journals 的 emotion_signals → EmotionService.ApplyAsync
6. 证据重算     EvidenceEngine 重算受影响断言的 c_actual（noisy-OR + logit，见 05）
                 └─ 冲突组内和 >1 → 触发 Review
7. 落库         Claims / Journals / Keywords / FragmentKeywords / EmotionSnapshot / 冲突组 一次事务
8. 提交；失败回滚（EmotionState.json 事务成功后原子写）
```

**原子性的意义**：一次提取的多条断言共享 utterance_id，必须整体成功或整体回滚，否则孤儿断言破坏同源分组。

### 情绪事务边界

- EmotionService 内部持锁，串行化所有 `ApplyAsync`（读最新 → 算 → 更新内存）
- **EmotionSnapshot 随 Writer 事务落库**（同连接同事务，数据库完全一致）
- **EmotionState.json 事务成功后原子写**（文件不参与事务，失败可重试；重启加载时 EmotionState.json 优先，缺失/损坏则从 EmotionSnapshot 恢复最新）
- 独立 `SubmitSignalAsync` 调用（如未来事件源直接提交）各自开事务，同样实时

### 事件接入预留

- Writer 提供 `SubmitEvent(level, type, detail, emotionSignals?)` 通道，EventSink 后续接入
- 当前仅落地程序启停（L0、无情绪），其余事件映射表待定——**接口先留好，实现后续填**

### 消费端约束

- 插件提交情绪只能经 `IEmotionAccess`，写记忆只能经 Writer 暴露的工具（journal/remember）
- 不暴露直接改绝对值、删表、删快照

## 产生端：碎片从哪来

提取能力**全部收敛进心智引擎**。两条链路共用同一套提取逻辑，只是触发时机不同。

### 双模式分工

| 模式 | 触发 | 产出 | 情绪 |
|---|---|---|---|
| **Express 伴随提取** | 消息攒批 + 空闲补一次 | 窄 Claim（用户说了X） | 不涉及 |
| **Express wait** | 模型主动调 wait 带 journal 参数 | L2 Journal | 实时 |
| **Session 运行期** | 暂停伴随提取；模型可随时调 journal | L2/L3 Journal | 实时 |
| **Session 善后** | end_session / compact 归档后心智引擎复活该 session 整理 | Claim + L2/L3 Journal | 由心智引擎统一写 |
| **框架事件** | 工具失败 / 超时 / 用户否定等 | L0/L1 + 情绪信号 | 实时 |

### Express 模式（伴随提取 + wait）

- **伴随提取**：一直跟随聊天记录做轻量提取，产窄 Claim（"用户说了X"），带消息引用作为 Evidence。**单轮调用，不开 session**（价值密度低，session 开销不值得）
- 价值密度低但成本也低（只有聊天记录），保持实时
- 主要用途：**沉默监听**——即使不说话，也在默默积累对频道用户的了解
- 触发节奏：消息攒批为主 + 空闲补一次，保证监听及时又不漏尾巴

### Session 模式（运行期暂停 + 善后复活）

- **运行期不做伴随提取**（暂停这条链路），模型可随时调 `journal` 记当下（L2）
- **善后 = 心智引擎复活已结束的 session**（不是 finalizing 态、不是新会话）：

  ```
  end_session / compact 成功 → session 真正结束（Status=Completed，存档留档）
    └─ 心智引擎监听 OnCompleted（或扫描 Completed 存档）
         └─ 复活：从存档重建 session 实例
              ├─ 加载旧历史（history.jsonl）——上下文真实连续，模型自带全部记忆
              ├─ 重定义工具集 → 记忆工具组（journal / remember / claim / archive）
              ├─ 保留原 profile（Core / 组件 / 权限）——之前的部分依然保留
              ├─ 不注入初始化提示词 —— 它不是新会话，无需 BuildStart 初始化
              └─ 注入整理任务 → 跑多轮 → 完成 → 再次归档（或删除存档）
  ```

  - **"真正结束就是 end_session 成功"**：不做拦截、不做 finalizing 前置。session 真被结束了，我们只看它是否真的结束
  - **必须定义为 session**：因为要重定义工具集（工具 = session 的工具集）。它是"复活"——从存档重建，非全新创建
  - **上下文是真实连续的，不是搬运的**：从 history.jsonl 加载，模型从头到尾看得见——它自己调过什么工具、说过什么、经过什么，就是它自己的记忆，对应关系天然成立，无需 parent 链接
  - **compact 特例**：compact 本来就让旧 session 退出 + 新 session 承接摘要。对话交给新的，记忆交给旧的（复活）——两者并存各司其职：

    ```
    compact(summary)
      ├─ 新 session 承接摘要，继续对话（现有逻辑不变）
      └─ 旧 session Completed → 心智引擎复活 → 整理完归档
    ```

  - 产出：
    - Claim（用户陈述 + 验证结论）
    - Journal（L2 当时经历 + L3 事后反思）
  - Session 的工具调用结果是高价值记忆源，集中到善后一次性深度处理（而不是运行期轻量链路浪费）
  - 需新增配置：**复活整理轮次上限**（MaxFinalizeRounds，防无限整理，超限强制归档）

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
| Claim.Credibility | **写入者定初值**（stated≈1.0 / inferred=推断度），Review 可改，见 05 |
| Claim.IsInference | 推断标记（驱动跨等级碾压 + 召回"推断"标签） |
| Claim.strength | 程度值（态度方向强度，或支持边强度），见 05 |
| Claim 支持/反对边 | assertion 槽位 → 收集为证据边（subject/object 引用断言 id），见 05 |
| Journal.keywords | 匹配/新建 Keywords → FragmentKeywords |
| Journal.emotion_signals | **事务内转交 EmotionService**（读最新→算→写→快照） |
| Journal.content | 正文存储，元数据（recorded_at 等）由 Writer 补全 |

**冲突接入（见 05）**：写入时 ConflictService 检查同（subject + 排他属性）槽位——取值不同则建组/入组；支持/反对边写入后 EvidenceEngine 重算受影响断言的 `c_actual`；冲突组内和 >1 → 触发 Review。

### Review 消费（待验证队列 + 暂存提交）

Review 是心智引擎的验证者：处理记忆库里的待验证项（冲突、可疑推断、手动标记），验证真伪，让信息更明确。完整机制见 [05-credibility-conflict-model.md](05-credibility-conflict-model.md) 6.0。

```
Review 启动 → 从 ReviewQueue 认领（Pending → UnderReview，替代旧信标自动生成部分）
  └─ 逐项处理：
       ├─ 查证（EvidenceRefs：源消息/经历/相关断言）
       ├─ 调锚点进暂存区：断言A 的 Credibility → x，断言B 的 Credibility → x
       ├─ review_preview 预览影响（dry-run 重算 c_actual，不写库）
       ├─ 满意 → review_commit（原子事务落库 + 重算 + 校验和≤1）
       └─ 队列项 Done；冲突组 ResolvedAt
```

- **ReviewQueue 是唯一工作源**：冲突组超预算、Dream 发现、手动 mark 都入队（见 05 6.0）
- Review 目的**不是"A 赢 B 输"**，而是把可信度**拉开差距**，让信息明确
- **git 式提交**：改动进暂存区，模型显式 `review_commit`（暂存区持久化，崩溃不丢）
- **归一化是最后手段**：仅当 Review 判定"没有实际证据，纯为消冲突"才等比缩放，默认不自动归一化
- **跨等级碾压自动处理**：stated 直接压 inferred，不建工单（见 05 4.6）
- 结果全靠可信度分配表达，**无四态枚举**（一真一假/合并/全假/未解决都只是分布形态）

### Dream 探索者（轻量切片）

Dream 是记忆库的**探索者**（与 Review 验证者分工）：找关联和问题、建推断断言，需要验证的移交 Review。**无 session 循环**，由 MaintenanceEngine 巡逻触发。核心已定：切片 + 统一产出去向；具体切片清单见下，调度节奏待定（见 04）。

#### 切片（Slice）

每个探索方向是一个**切片**：纯函数式单元，输入一批记忆线索，输出候选推断。统一基类：

```csharp
abstract class DreamSlice
{
    public abstract string Name { get; }                       // 切片标识
    public abstract Task<IReadOnlyList<DreamCandidate>>
        ExploreAsync(CancellationToken ct);                    // 线索 → 候选
}

class DreamCandidate
{
    public CandidateKind Kind;     // Inference（新推断）| Issue（现存断言疑点）
    public ClaimSpec Claim;        // Kind=Inference 有：三元组 + strength + 推断确定度
    public TargetRef Target;       // Kind=Issue 有：指向已存在断言/实体
    public string Reason;          // 推断理由 / 疑点描述（审计/展示）
    public bool NeedsReview;       // true → 入 ReviewQueue（Source=Dream）
}
```

- 切片**无状态、不跑循环**：一次 Explore 一次返回，候选由 Dream 出口统一判定
- 新探索方向 = 新增一个 `DreamSlice` 实现 + 注册，不改核心

#### 默认切片

| 切片 | 探索方向 | 产出 | 执行 |
|---|---|---|---|
| A1 同主体聚类 | 同一 subject 的多个断言之间 | Inference（S 对 O 的偏好） | 确定性 |
| A2 同客体聚类 | 同一 object 被多个 subject 引用 | Inference（subject 相关） | 语义 |
| A3 桥接 | S₁→O 且 S₂→O，S₁/S₂ 无直接边 | Inference（S₁↔S₂ 关系） | 语义 |
| B1 传递 | A→B 且 B→C | Inference（A→C，低确定度） | 确定性 |
| C1 支持链检查 | 支持边链两侧矛盾 | Issue（支持边可信度存疑） | 确定性 |
| C2 悬空引用 | 断言引用不存在的实体/断言 | Issue（标记悬空） | 确定性 |

#### 产出去向（统一出口）

**候选 = 半成品**：切片只负责"发现什么"，"怎么入体系"是引擎职责。全部候选进 Dream 出口逐条处理：

```
ExploreAsync() → List<DreamCandidate>（空 = 跳过，跑下一切片）
  └─ 按 Kind 分叉：
     Inference → 1.过滤重复（同 subject+predicate+object 已存在）→ 丢弃
                 2.补全   IsInference=true, Credibility=confidence,
                          source={who:dream, slice, hits}
                 3.落库   Writer.WriteBatch —— 复用冲突检测 + 证据重算 + 事务
                 4.移交   NeedsReview=true 或 落库顶爆冲突组 → ReviewQueue(Source=Dream)
     Issue     → 不入库，直接构造 ReviewQueue 项（TargetRef=现有断言, Reason=疑点描述）
  └─ 一轮结束  框架生成日志式 L3 Journal（"本轮 Dream 发现了什么"）
```

- **`needsReview` 是切片意图**（太脆/主动挂），**ConflictService 是系统强制**（数值顶爆自动挂），两者独立并存
- **冲突不聚批**：多条候选逐条独立走管线，一条顶爆不影响同轮其他条继续落库
- 重复过滤的判据与可信度正交——新证据不覆盖旧断言，直接跳过（见"几乎不覆盖"核心决策）

#### 执行路径（直接接 Core，不经 session）

Dream 不建 RuntimeSession 实例，按切片性质分两路：

| 路 | 切片 | 执行 |
|---|---|---|
| **确定性** | A1 / B1 / C1 / C2 | 纯代码 + 检索服务（SQL / 图遍历），完全不碰模型 |
| **语义** | A2 / A3 | 直接调 Core **单轮**（无工具表、无状态、无持久化），让模型补全关系名/定性/写 raw |

- 语义切片每轮一次单轮调用，模型只要一个输出，不需要多轮推理
- 落库路径与"Core 还是 Session"无关：切片产物统一进 `Writer.WriteBatch`，单写入口不破
- 与 Express 伴随提取同构（单轮、无 session）；Review 才用 session（查证/调锚点/暂存提交），Dream 用不上那个重量级

### 框架事件

- 工具失败 / 超时 / 用户否定等 → 写 L0/L1 记录并提交情绪信号，实时应用
- **事件 → 情绪目标值映射表暂不做**（首版只落 L0/L1 事件，不带情绪信号；映射表等事件源积累后补，见 [06-pending-todos.md](06-pending-todos.md)）
- **程序启动/停止（已定稿）**：落 L0 框架事件，纯机械日志，不带情绪信号：

  ```
  FrameworkEvents: { Level: L0, Type: "ProcessStarted" | "ProcessStopped",
                     Detail: { version, pid, reason?, uptime? }, Timestamp }
  ```

## 人物与账号

人物库不再单独建表。人物是普通 entity 节点，账号是框架层轻量实体，信任是评估断言。

### 账号（框架层，非记忆）

- 框架轻量实体：Platform + PlatformId + DisplayName + PermissionLevel，只服务运行时（权限裁决 / 频道参与者 / 消息路由）
- **不是"人"**：人是实体节点，账号是它的登录身份，两者不合并为一张表
- **跨平台同一人 = 断言**：`账号A 属于 人物X`（或 `人物A == 人物B`），用普通边表达，不设专门表（与"entity 只存 index 不归并"一致）
- **AlertLevel 取消**：暂时不需要，框架不记安全警报状态

### 信任（评估断言，非字段）

信任是 Lilara 对某人物的一条**评估断言**，不是字段，也不是好感度流水账：

```
Claims: { subject:   {entity, Lilara},
          predicate: {relation, "信任"},
          object:    {entity, 人物},
          object_value: 4,        // 等级 -2~5（沿用旧 TrustLevel 语义）
          Credibility: 0.9 }      // "我多确定这个定级"，与等级正交
```

- **日常事件不更新**：消息、互动、帮助都不碰它——低信息量交互会稀释信任信号
- **Review 审视时重估**：只有 Review 在处理该人物的冲突/悬空/关键交互时，才显式调整等级 + 可信度（"关键问题才能看出来人的特质"）
- **框架只读这条断言**：路由时（如 Hostile 被@才敷衍）查询当前等级——单一来源，无同步字段
- **证据网络可影响它**：新断言可"支持/反对"这条信任评估（如"咪咪辱骂我"反对它）——关键问题显露特质被系统化，接 05 机制
- 等级与可信度正交：`信任=4 但 Credibility=0.5` = "之前觉得他可靠，现在不确定了"
- **冷启动默认 Unknown**：不预建信任断言，首次 Review 审视该人物时才建

### WebUI

人物/账号必须有 WebUI 视图——否则冷启动后连"认识谁"都不可见，用户无从管理：
- 人物列表（entity 节点）+ 信任评估断言可视化（等级 + 可信度）
- 账号列表（Platform + PlatformId + 权限）
- 归属/关联断言可查（跨平台同一人）

细节实现待定（见 04）。

## 心智插件（注入）

情绪注入、经历注入做成**插件**（消费端自治），不塞进 MindEngine 核心。核心只生产状态（EmotionService），消费由各 Session 按自己性质决定（对话型注入、审查型 Review 不注入）。

**一个 DLL，多个 `[Component]`**——注入与工具天然分离，各组件在 ComponentConfig.json 独立启停：

```
Plugin.MindTools/
├── MindEmotionComponent    [Global 组件]  → BuildPromptSection：读情绪 → 生成状态片段
├── MindExperienceComponent [Global 组件]  → BuildPromptSection：读经历 → 生成经历片段
└── 心智工具                独立 ITool     → PermissionService 黑白名单（工具级）
```

- 注入用组件装配控制（ComponentConfig），工具用权限组控制（Storage/Session/{group}.json）——两套控制面
- 情绪组件按 `caller.LoopType` 区分：Working/Express 注入、Review 不注入（冷静验证不带情绪偏置）

### 情绪注入（MindEmotionComponent）

- 读 `IEmotionAccess.GetCurrentAsync()`，生成状态描述片段（非五轴原始数值，02）
- 每轮注入（情绪是动态的），状态变化即更新

### 经历注入（MindExperienceComponent）

- 复用 RetrieveService（关键词 + 时间窗口），不碰表
- **L1/L0 不主动注入**（FrameworkEvents，仅作 Evidence 被引用）
- **来源优先于类型**：标签 `[相对时间 · 来源]`，来源 = 本频道 / 频道名 / 复盘·内部
- **初建（BuildStartInject）按 L 等级扩展时间窗**：
  - L3（反思）宽窗，时间久也纳入（信息密度高，值得穿透）
  - L2（经历）窄窗，仅最近 N 天（流水账容易吵）
  - 预算：L3 2~3 条 + L2 3~5 条，同级内按时间倒序（可调配置）
- **后续轮（BuildRoundInject）只注入新增 L3**：L2 太嘈杂不值得每轮刷新，L3 是浓缩信号（防互相干扰）
- **本频道权重**：注入时本频道经历优先纳入，跨频道降权（框架替模型做上下文判断）

### 注入 vs 检索的边界

- **注入是框架塞给模型的**——可以有频道倾向（替模型判断当前上下文）
- **检索是模型自己查的**——**中立公平，不做频道奖励**；模型显式加"仅限频道X"条件才过滤

## 与现有系统的接缝

- **OnCompleted 事件**（`RuntimeSession.cs`）已存在，复活监听直接订阅
- **Completed 存档**（`SessionEngine.cs`）已"留档不加载"，复活从该存档重建
- **Session 类型配置化**（`Storage/Session/{type}.json`）已就绪，记忆工具组/整理会话只需新增配置
- **session 善后不做拦截**——端到端结束保持现状，复活在其后发生
