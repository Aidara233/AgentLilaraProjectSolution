# 决策记录（00）

> 状态：设计定稿（尚未实现）。本文件是后续所有契约与实现的依据；与代码现状冲突时，以本文件为准。
> 背景：项目准备大规模重构。适配器与插件工具集视为资产（改造，不重写）；内核管线与会话管理混合体、WebUI 巨石视为负债（重写）。

---

## 1. 分层与解耦

| # | 决策 | 理由 |
|---|---|---|
| D1 | **入库与起会话彻底分离**：消息一律入库，差异仅在"是否进入条件判定流程" | 孤儿/未接入数据也要可存可查，但不该凭空激活会话 |
| D2 | 入向管道四段：**Ingest → Gate（绑定判定）→ 条件求值 → Session** | 每段职责单一、可单测 |
| D3 | 频道**不再自动创建**，改显式 `attach / detach / create` | 由"消息顺带建频道"改成"资源显式登记" |
| D4 | 未绑定频道/流源的数据：**正常入库、参与查询、不参与激活** | 避免"不监听"连带砍掉归档与记忆能力 |

---

## 2. 存储

| # | 决策 | 理由 |
|---|---|---|
| D5 | 继续用 **SQLite**，不引入独立数据库部署 | 部署重量优先；并发压力用分库 + 缓冲写解决 |
| D6 | **按适配器分库**（`Storage/Data/adapters/{id}.db`），流源同样独立分库（`sources/*.db`）；内核数据在 `kernel.db` | SQLite 写锁按库隔离 → 天然并行化；数据所有权随适配器 |
| D7 | **消息全存**，不做按角色的保留期区分 | 纯文本不占空间；备份与可回溯优先 |
| D8 | `RawPayload = full / textOnly / external`，默认 **external**（>8KB 的原始载荷外部化） | 原文可重放，但避免 JSON 把库撑到几十 G |
| D9 | 写入 = **内存缓冲 + 每库单写者批量提交**；`DurabilityMode = buffered / sync` | 吞吐与持久性可权衡 |
| D10 | 查询需可见"已入缓冲未落盘"的消息 → 每适配器维护 **`PendingIndex`** | 否则刚发的话查不到 |
| D11 | 跨适配器去重：库内靠 `UNIQUE(PlatformChannelId, PlatformMessageId)`；跨库靠 **`DedupeKey`**，且仅对"够长文本 / 含附件哈希"启用 | 各平台消息 id 命名空间不同，无法直接比对；短文本误合并风险高 |
| D12 | 各库带 `SchemaVersion`，迁移逐库执行 | 多库无单事务，只能最终一致 |

### 最小归档表契约

框架要求每个适配器库都有 `Messages` 表（列名/类型固定，可扩展额外列）：

```sql
CREATE TABLE Messages(
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  PlatformChannelId TEXT NOT NULL, PlatformMessageId TEXT,
  SenderPlatformId TEXT, SenderDisplayName TEXT,
  Kind TEXT, Text TEXT, RawRef TEXT, Time INTEGER, IngestedUtc INTEGER,
  DedupeKey TEXT, IsFromBot INTEGER, ReplyToPlatformMessageId TEXT);
CREATE UNIQUE INDEX UX_PlatformMsg ON Messages(PlatformChannelId, PlatformMessageId);
```

---

## 3. 路由（频道 × 方向 × 适配器）

| # | 决策 | 理由 |
|---|---|---|
| D13 | 优先级为**数字，大者优先**；`<= 0` 表示"仅归档，不参与竞争" | 直观；同一适配器可在 A 频道为主、B 频道为次（键是 `(channel, direction, adapter)`） |
| D14 | 入向：只有当前 primary 的消息进条件判定；backup/archiveOnly **仍然入库**（存入各自适配器库） | 主源静默丢消息时，备源是"另一路真相"，对账才有依据 |
| D15 | 出向：每次发送实时选择，配 **sticky 窗口**（同一 burst / 60s 内不换源） | 防一次长回复被劈到两个账号 |
| D16 | 健康判定带**迟滞**（N 次失败降级、K 次成功恢复） | 防网络抖动导致来回切换 |
| D17 | 跨账号 fallback 默认**关闭**（仅同账号多适配器自动切） | 群里看到换号回话是语义变化，需显式开启 |

---

## 4. 强查与缺口（防丢段）

| # | 决策 | 理由 |
|---|---|---|
| D18 | 强查 = 查**适配器自己的消息库**（`IAdapterQuery.QueryMessagesAsync`），**不是调用平台 API** | 便宜、无配额、离线可做；数据本就由适配器管理 |
| D19 | 平台 API 探测为可选 `IAdapterPlatformProbe`，**默认关闭**，带预算与退避 | 真需要时（服务端留档）才开 |
| D20 | 缺口分三类：`Unconsumed`（库里有、管道没消费，最常见）/ `MissingInAdapter`（适配器没收到）/ `Unrecoverable` | "错位崩溃丢段"多数属第一类，修复成本最低 |
| D21 | 补洞幂等（唯一约束），补入标记 `repaired` 且**不触发激活** | 防补历史时突然回复三天前的消息 |
| D22 | `Unrecoverable` 时在会话上下文插入显式缺失提示 | 让模型知道有洞，胜过幻觉补全 |
| D23 | 出向分段**台账** `OutboundLedger(SegmentIndex, SegmentCount, ContentHash, Status)`；崩溃后续发剩余段，不整条重发 | 现有 `SpeakTool` 按空行分条逐条发送、`SendFileTool` fire-and-forget，无台账 |

---

## 5. 信源与信号网

| # | 决策 | 理由 |
|---|---|---|
| D24 | 三类源同一条管道：`Channel` / `Stream` / `Signal` | 概念统一，规则/条件/graph 一套到底 |
| D25 | 信号网由用户**显式接线**（"自己拉电线"），不做隐式传播 | 图可控，防环下沉为配置期校验 |
| D26 | 防环：配置期图校验（环路默认拒绝保存）+ 运行时判定（同一 trace 内重复进入同一续延实例即中止；`trace.maxSegments` 兜底） | 显式接线后风险来自手滑，而不是无限连线；判定基于 trace 身份而非计数器 |
| D27 | 频道/适配器的警告与错误也是一等信源 | "适配器挂了就开个会话处理"只是几条规则，不是新代码 |
| D28 | 信号端口按原因拆分（如 `resource.dropped.busy/timeout/queueFull`），接线即筛选 | 大多数"过滤"不需要节点 |

---

## 6. 图模型

见 `04-graph-model.md` 与 `11-object-model.md`。要点：五概念（激活源/方法/属性读/属性写/变量）、两类图（静态类/动态类）、值平面必给值（默认值为常量）、事件平面不臆造、求解式执行（无解即无效）、模块边界即图的粒度。

---

## 7. 适配器 vs 框架

**判定三问**：换平台是否必须重写 → 适配器；是否跨源一致策略 → 框架；是否需授权/审计/回滚 → 框架。

### 适配器必须提供（平台知识，不可抽象）
连接/重连/鉴权 · 解析为 `InboundEvent` 且保留原始载荷 · 平台身份键与自身回声识别 · 发送（分段/@/回复/媒体/平台长度上限） · 平台不可移植动作 · **能力声明**（可枚举/可发媒体/最大长度/限流） · **平台原生查询**（列群/好友/房间、按平台 id 查消息） · **归档保留策略** · 游标能力（`MonotonicId` / `TimeRange`）。

### 框架必须提供
入站管道（去重/落库/索引/抽 Facts/发布） · 频道注册与绑定 · 条件求值 · 会话生命周期与租约 · 查询门面 · 出向路由与租约 · 工具宿主与门禁 · 授权/审计/断路器/快照 · 定时与信号总线 · 存储与迁移 · WebUI 外壳与页面 schema · 插件装载。

### 适配器改造清单（改造，非重写）
- 全部：补 `AdapterCapabilities`、`IAdapterQuery`、`DataPolicy`；`InboundEvent` 保留原始载荷；移除"按 channel 名字猜归属"与"消息进来顺手建频道"。
- OneBot：平台查询接 `get_group_list/get_friend_list/get_msg`；回声判定统一走框架 `SentRegistry`；去掉 120s 内存去重窗口（改 DB 唯一约束 + `DedupeKey`）。
- Icalingua：`roomId` 正负号语义移入 `PlatformChannelId` 规范化；`BotNames` 判定上提到框架。
- HttpBridge：`IAdapterQuery` 用配置 channels；`wait=true` 改为订阅激活事件而非轮询 DB；未接入频道返回 `skipped: "unattached-channel"`。
- FileAdapter：目录即会话，`PlatformChannelId` = 文件名前缀；2s 轮询保留（测试用）。

---

## 8. 查询框架（面向模型）

少数工具、强过滤、作用域显式：

```
query_channels  { scope: registered | unattached | platform | all, adapter?, kind?, keyword?, limit? }
query_messages  { scope: channel | platform | all, channelRef? | {adapterId, platformChannelId}?, ... }
query_sources   { kind?, enabled? }
query_sessions  { channelRef?, type?, state? }
query_adapters  { }
query_plugins   { }
query_resources { }          // 资源持有者 + 等待队列 + 最近丢弃原因
```

- 消息引用统一为 `msgRef = "{adapterId}:{localId}"`，避免 int/字符串两套 id 混用。
- 保留现有工具名 `list_channels` / `search_chat_history`，实现改为调查询框架（提示词与行为不变）。

---

## 9. 自修改与授权

| # | 决策 | 理由 |
|---|---|---|
| D29 | 所有可变设置声明为 `SettingDescriptor { Key, Schema, Scope, Risk, Approval, SelfModifiable }`；`config_read/config_write` 一对通用工具覆盖全部，WebUI 表单由 schema 生成 | 工具数量不膨胀；治理集中一处 |
| D30 | 断路器 `Storage/Auth/governor.json`，档位 `off / propose / apply-with-audit / autonomous` + `allowlist/denylist`；模型只读 | 极端情况可放开自修改，一键收回 |
| D31 | 每次写入落 append-only 审计 + 快照；`rollback(snapshotId)` 是一等工具 | 可逆 |
| D32 | 元权限（改授权等级、改断路器本身、改审计保留）**人类专属**，模型永不可及 | 总闸不可自我解开 |
| D33 | 条件脚本用**进程内 Roslyn**（引用白名单 + 语句/时间预算 + 写入需审批） | 微秒级、无第二进程 |

---

## 10. WebUI

| # | 决策 | 理由 |
|---|---|---|
| D34 | 顺序：先冻结 `AgentLilara.Api` v1 → **切出独立进程** → 再重做 UI | 接口先稳定，UI 才可替换；WebUI 崩溃不影响 bot |
| D35 | 页面 = schema（`PageSpec` + block：Table/Form/Tree/Editor/Stats/Stream/RuleBuilder/**Canvas**），插件以 `ui/*.json` 贡献页面 | 治"Provider 巨石"；插件 UI 不再编译进宿主 |
| D36 | 契约三件：Query（读）/ Command（写，走 draft+审批）/ Subscription（WS 推送）；`/v1/capabilities` 自描述 | UI 纯消费者，可换栈 |

---

## 11. 插件系统整理

| 病灶 | 目标 |
|---|---|
| `PluginLoader` 自己发现 5 类贡献（Tool/Component/Provider/InjectProvider/Lifecycle） | 单一 `IContributionRegistry`，装载后一次性提交，失败整体回滚 |
| 三张全局静态注册表各自 `TryAdd` | 统一事务化注册 |
| 工具既可组件提供又可 loader 直接注册 | 生命周期唯一化 |
| 插件直接引用宿主项目（`Plugin.WorkingTools`） | CI 强制：插件只能引用 `AgentLilara.PluginSDK` |
| 管理面自行扫盘解析 | 管理面只调 `query_plugins` |
| `plugin.json` 字段零散 | 清单化：`sdk` 版本契约、`permissions`、`contributions{tools,components,signals,nodes,ui,services}` |

---

## 12. 重构切分与阶段

```
AgentLilara.PluginSDK     契约（工具/组件/端口/适配器面/查询/SettingDescriptor/PageSpec/Node 描述）
AgentLilara.GraphRuntime        管道 + 图 + 会话 + 查询 + 授权（无平台知识，可单测）
AgentLilara.Adapters.*    每平台一项目（OneBot / Icalingua / HttpBridge / File）
AgentLilara.Sources.*     流源（Email / HomeAssistant / Webhook）
AgentLilara.WebUI         外壳 + schema 渲染器（独立进程）
AgentLilara.Host          组合根（DI、启动、CLI）
```

| 阶段 | 内容 | 验收 |
|---|---|---|
| A | 冻结 `AgentLilara.Api` v1 与 SDK 契约 | 契约评审 + conformance 骨架 |
| B | 新 Kernel：Ingest → 分库 writer → 查询 fan-out | 单测：去重/合并排序/游标 |
| C | 适配器逐个接新管道（旧管道并行，shadow 对比） | 每适配器 conformance 绿 |
| D | 插件系统整理 | 25 个插件全量加载；耦合解除 |
| E | WebUI 切出进程（旧 UI 打新 API） | 功能对齐；bot 可无 UI 运行 |
| F | 删旧：`MessageEngine`/`AdapterEngine` 半成品、`SessionManager` 混合体、`ImpulseConfig`、自动建频道路径 | 全量测试 + shadow 零差异 |
| G | 条件图（逻辑/拼接/信号）+ 自修改门 | dry-run / 影子 / 审批三层可用 |

**迁移要点**
- 存量频道由自动创建而来：迁移回填 `AdapterId`（按名字前缀推断）并置 `attached`。
- `--test` 夹具依赖 `file` 频道自动创建：必须在 bootstrap 预置 attach，否则冒烟全挂。
- 回退开关：`Channels.AutoCreateMode = manual | legacy`。
- 旧客户端兼容：HttpBridge 未接入频道返回 `{ok:true, activated:false, skipped:"unattached-channel"}`，并提供显式 `attach_if_missing` 参数。

---

## 13. 风险台账

| 风险 | 缓解 |
|---|---|
多库无单事务 | 逐库 schema 版本；备份先 `wal_checkpoint` 再复制 |
缓冲丢窗口 | `DurabilityMode` 可切；写失败落 spool 重放 |
去重误判 | 仅跨适配器合并；短文本不合并；保留 `suppressed` 记录可查 |
FTS 分库 | 库数=适配器数（个位数），并行查询可接受 |
WebUI 切出后鉴权面 | 默认绑 `127.0.0.1` + token；command 全审计 |
信号风暴 | 端口分原因 + 用户自建动态图（节流/去重/计数）+ `trace.maxSegments` 兜底（框架不内置限流） |

---

## 14. 追加决策（会话装配与执行语义）

| # | 决策 | 理由 |
|---|---|---|
D37 | **取消会话类型特化**：不设"频道会话/express 会话"这类预置类型，改为**装配式 `SessionSpec`**（系统提示词 / 人设附加 / 模型配置 / 工具配置 / 预算 / 初始状态），初始化时自由组装，运行中可热切换 | 会话形态应由内容决定，而不是由"挂在哪个频道"决定 |
D38 | **同网络 tick 串行，跨网络并行**；一次 tick 严格三阶段：① 准备节点值 ② 计算过程 ③ **汇聚激活汇块**（所有汇块收齐信息后一次性提交，而不是算完一个激活一个） | 语义简单、可重放；副作用集中在唯一的阶段③ |
D39 | **汇块是"大接口"外壳，本身不报错**；失败的是模块（节点/模块实现/插件代码），失败即激活错误源块 `runtime.error`，由电路自行处置 | 报错是信息，应进入信号网而不是抛给用户 |
D40 | **旧数据不迁移**：现有库直接弃用/删除，新库从空白开始；频道与适配器绑定在 WebUI 重新登记 | 旧库是旧模型的形状，迁移成本远高于重建 |
D41 | **框架不内置限额**（不做令牌桶、不做自动聚合）；需要限流就用动态图模块（节流/去重/计数）自己拼 | 规则显式、可读、可改；避免隐藏行为 |
D42 | 对外接口**只用 token 鉴权**，暂不加速率限制 | 当前只在本机/可信网络使用 |
D43 | 授权按**权限等级**（`PermissionLevel`），`TrustLevel` 只影响对话风格；**现阶段改图只能经 WebUI 的 Api**，会话内不提供改图工具；模型自修改整体后置 | 先把边界收窄，能力后续再放开 |
D44 | **会话收尾、摘要、记忆提取与审阅由内核处理**，不交给电路；电路只负责"要不要起会话、起什么会话" | 收尾是固有流程，不该依赖用户是否记得接线 |
D45 | **求解式执行**：初始时除源块外所有节点输出悬空（未知），工作队列按"输入全部确定才就绪"逐个求解，每点只写一次；队列空而仍有未知 → **求解无效**，本拍不产生任何副作用并抛严重错误 | 值唯一 + 天然防环（同拍依赖环必然无解）；顺序固定可重放 |
D46 | **对象模型**：激活源=生命周期（每图**恰一个**）、激活汇=方法、数据源=属性（读）、数据汇=属性（写）、变量=内部存储；取消 `fired` 与拼接块类别 | 与编程直觉一致；状态只经由变量表达，块不再各自带状态 |
D47 | **两类图**：无状态图=静态类（实例唯一，一次求解后清理中间态，无环）；有状态图=动态类（多实例、各自独立，时钟沿快照→同步更新，环合法并自带 1 拍延迟） | 同拍矛盾环与跨拍反馈各归其位，不需两套环检测 |
D48 | **多触发 = 多个图调用同一个方法**：坚持一图一激活源；想"多条件都走同一反应"就定义导出方法（激活汇），让多张图各自调用 | 激活源语义单一，默认值与生命周期推理都更简单 |
D49 | **变量的提交语义**：一次激活内所有写入在激活成功结束时一次提交，失败（求解无效/模块异常）全部丢弃；动态图每拍双缓冲交换；跨图读取一律读"上次提交值" | 保证"值唯一"，且跨图依赖不会打破求解不变量 |
D50 | **图 = 源码，WebUI = IDE**：类（GraphClass）/ 挂载（Mount）/ 实例（GraphInstance）三层；静态类实例唯一，动态类按作用域键实例化（Global / ChannelManager / Channel / Session / Plugin） | 图不是配置而是资产：可 diff、可评审、可复用；"给频道画图"变成"定义类 + 挂载" |
D51 | **本拍上下文 `this`**：激活源注入 `tick.channel/session/event`，数据源与汇块可省略目标参数；静态类**不允许**按频道分区存变量，跨频道的"各自状态"必须落该频道的动态实例 | 让"所有频道共享一个静态类"成立，同时不破坏作用域模型 |
D52 | **变量全部公开**：所有变量都是可读属性（`query.variables`、WebUI 面板、trace 逐拍展示）；写入仍只属于所属实例，跨图读上次提交值；调试直改需审计并仅开发模式 | 可观测性优先；暴露不影响一致性 |
D53 | **实例与挂点共存亡**：频道被杀/会话结束 → 实例立即销毁；死前先激活 `on.kill`（可 `cancelKill` 取消本次销毁）；强杀或取消次数超阈值（默认 3）则跳过该段 | 与代码的对象生命周期一致；不留僵尸实例 |
D54 | **挂载表是数据、可选**：`GraphMounts(Scope, ScopeKey, ClassId, InstanceName, Params, Enabled, Order)`；单个频道可用 `GraphOverrides` 覆盖参数/变量初值，或换用变体类（预制件思路） | 默认"什么都不挂"；个别频道可定制而不影响全局 |
D55 | 断点/单步**暂不做**；编辑器能力先做 trace + 试跑 + 查找引用 | 先把编译与可观测性做扎实，调试器后置 |
D56 | **模块边界 = 图的粒度**：模块（如 agent 循环）自成体系、只暴露接口（激活源/方法/属性读/属性写），内部状态与内部时序不暴露；图只表达模块之间的连线，禁止图套图；单图节点数预期 ≤ 20–30 | 图擅长接线，不擅长算法；这条边界决定了编辑器是"装配层"而不是"通用编程语言" |
D57 | **模块节点由描述符投影，不手写**：命名 `module.<id>.<method>` / `read.<id>.<property>` / `write.<id>.<property>`；方法名去 `Async` 后首字母小写；`CancellationToken` 不占端口；未标注 `[Method]`/`[Lifecycle]` 的公开方法、以及承载不了的类型（`object`/委托/接口）一律在生成期失败；**未绑定实现的模块，含它的图加载即失败** | 清单与实现永远同步，且"图上少一个节点"必须是显式错误而不是静默 |
D58 | **汇块触发口统一叫 `event`**，取消 `t` 这类第二使能口：`已知`=使能、`no-event`=本拍不触发；可选参数口允许不接线（`IOptionalInputs`），节点自己用声明默认值 | 一个口一个含义；闸门用的就是 `gate.if`，不必在每个汇块上重复一遍使能逻辑 |
D59 | **方法节点无输出口**：模块方法的返回值只进 `SinkOutcome.Outputs` 台账，不接端口；返回值进入下一拍（续延放行）尚未实现。返回 `Task` 的方法暂走同步桥 | 保住"汇块无输出"这条不变量；先把调用打通，续延接线做成显式的一步 |
D60 | **事件按"产生者主体"划分**：同一主体的同刻属性是字段（群/私聊、是否@、是否回复），不同主体的行为是不同事件（收消息 ≠ 入库），同一主体的状态变迁合成一个事件 + `state` 字段（除非两种状态要接完全不同的反应）。加新信源不新增事件种类，只换 `adapterId` | 按内容拆会导致同一消息扇出多条事件，且每来一个源都要加一串事件类型
D61 | **三层：骨架 / 事件 / 插槽**。骨架是写死的流水线（入站 → 去重落库 → 会话归属 → 执行 → 出向），每步发事件、可观察、不可控；插槽是骨架上少数决策点，可被图/agent 填充。判据：**接线变化空间为零且必须永远成立的东西写死，有分支有取舍的才做成插槽**。顺序硬约束：**先落库再决策**，图失败不影响数据 | 把核心路径也接进图，等于用复杂度换零收益，且"少写死代码"被推到极端会连基础功能都可缺失
D62 | **接口闭包**：动作接口集有限、只增不改、每个动作一个节点类型（`IntentCatalog` 是唯一落点）；纯计算进闭合的**算子集**，图与 agent 只能组合、不能新增算子。**禁止** `filter`/`map`/用户自定义函数 | 一旦允许新增算子，接口集合会自己长成图灵完备语言，静态校验与安全论证全部失效
D63 | **图不执行动作，只产出 `ActionIntent`**；执行归骨架。动作分**即时**（同步、无 I/O，可在一次激活内取结果）与**派发**（异步、有 I/O，只产意图不等待）。`model.invoke` 允许接线直接调用，但限定为**单次、无状态、无工具、无历史**；多轮与会话状态只在 `session.*` 内部 | 解决"等模型 30 秒"的死结；一个 tick 里等多个 I/O 必然炸；同时保住"汇块无输出"的不变量
D64 | **不设数据敏感度分级（trust/taint）**：接口固定让数据流天然显式，数据只能沿画出来的连线走，没有"读取任意上下文"的口子，因此不需要标签系统与污点分析。保留的是**资源预算**（防振荡刷屏）、**权限边界**（`wire.apply` 不能改 caps 与治理配置）、**审计 diff**（人 review 接线即可） | 该机制只在"能执行任意代码"的世界里必要；在接线模型里它换不来等价的收益
D65 | **保持"一图恰一个激活源"**（不放开多源）：多源只有在"两个源给的参数一样"时才省事，参数不同就是负担。要组合就加**中间层图**，故 `sink.pulse` 是 `preset.digest` 的前置条件而非可选项 | 激活源语义单一，默认值与生命周期推理才简单
D66 | **预制图装在激活层（第③层）**，不装在骨架里；装哪张由挂载表决定。`preset.quiet`（消息入库后不激活）与 `preset.router`（优先投递到频道活跃会话；无会话时按图指定类型创建并投递）已实现；`preset.digest`（攒批）后续实现 | 挂载选择处理策略，骨架保证先入库再路由
