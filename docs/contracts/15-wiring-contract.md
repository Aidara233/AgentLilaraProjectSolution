# 接线契约（15）

> 本文定义"图能改什么、不能改什么"。节点目录见 `05-nodes.md`，执行语义见 `04-graph-model.md`，模块见 `14-modules.md`。
> 状态：**骨架未实现，图的两端已就绪**（事件进、意图出）。

---

## 1. 三层模型

| 层 | 是什么 | 谁能改 | 是否可观察 |
|---|---|---|---|
**骨架** | 固定的流水线，保证不变量与顺序 | 人（代码） | 每步发事件 |
**事件** | 骨架每一步的产物，也是图的唯一输入 | 只读 | — |
**插槽** | 骨架上少数决策点 | 图 / agent | — |

原则：**骨架可观察、不可控；插槽可控、必须少。** 图主要"对事件做反应"，只在一处"拦截核心流程"。

---

## 2. 一条消息的流水线

| 步骤 | 谁做 | 事件 | 可控制 |
|---|---|---|---|
1 接收入站 | 骨架（adapter） | `message.received` | 否 |
2 去重 + 落库 | 骨架（store） | `message.stored` | 否 |
3 **要不要响应、怎么响应** | **插槽（图）** | — | **是** |
4 会话（新建/续用） | 骨架（session） | `session.started` / `turn` / `finished` | 否 |
5 出向排队 + 台账 + 重试 | 骨架（outbound） | `outbound.queued` / `sent` / `failed` | 否 |
6 真正发出 | 骨架（adapter） | — | 否 |

**顺序是硬约束：先落库，再决策。** 图跑不跑、跑成什么样，都不影响数据；图失败 = 不响应 + `runtime.error`，不可能导致消息没入库。

判据（哪个功能写死、哪个开放）：

> **接线变化空间为零、且必须永远成立的东西 → 写死。有分支、有取舍、按场景变化的 → 插槽。**

---

## 3. 事件集

事件**按"产生者主体"划分**，不按内容变体划分（D60）：

- 同一主体的同刻属性 → 字段（群/私聊、是否@、是否回复）；
- 不同主体的行为 → 不同事件（收消息 ≠ 入库）；
- 同一主体的状态变迁 → 一个事件 + `state` 字段，**除非**两种状态要接完全不同的反应。

| 事件 | 载荷 | 频率 | 状态 |
|---|---|---|---|
`message.received` | adapter、channel、channelKind、sender、text、mentionsBot、isReplyToBot、receivedAt、dedupeKey | 高 | 已实现 |
`clock.tick` | seq | 高 | 已实现（动态图） |
`session.finished` | sessionRef、reason | 中 | 归档保留 |
`session.failed` | sessionRef、stage、message | 低 | 归档保留 |
`adapter.stateChanged` | adapterId、state、since | 低 | 归档保留 |
`runtime.error` | kind、node、message | 低 | 归档保留 |

加一个新信源（邮件、webhook）**不新增事件种类**，只换 `adapterId`。

---

## 4. 动作集

**图不执行动作，只产出 `ActionIntent`**；执行归骨架（D63）。动作分两类：

- **即时**：同步、无 I/O、毫秒级 → 可以在一次激活内取到结果；
- **派发**：异步、有 I/O → 只产意图，不等待。

清单的唯一代码落点是 `IntentCatalog`（`Intent.cs`）：加动作 = 加一行 + 改本文。

| 节点 | 动作 | 输入口（除 `event`） | Target 来源 | 类型 | 归属 |
|---|---|---|---|---|---|
`sink.send` | SendMessage | `text` | 事件频道 | 派发 | 插槽 |
`sink.prompt` | SessionPrompt | `sessionRef`、`text` | `sessionRef` | 派发 | 插槽 |
`sink.route` | SessionRoute | `sessionType` | 当前事件频道 | 派发 | 插槽（先复用频道活跃会话，否则按类型创建并投递当前消息） |
`sink.inject` | SessionInject | `sessionRef`、`text` | `sessionRef` | 派发 | 插槽 |
`sink.set` | StateSet | `key`、`value` | `key` | 即时 | 插槽 |

**不开放**（骨架内部，图上没有节点）：落库、去重、出向台账与重试、真正发送、会话持久化、审计。

判据：**接口只给"有副作用的动作"**；纯计算一律进算子集（§5），否则接口集合会自己长成编程语言。

---

## 5. 算子集

纯计算，闭合、只增不改，图和 agent 只能**组合**，不能新增：

| 组 | 算子 |
|---|---|
逻辑 | `logic.and`、`logic.or`、`logic.select`（`cond ? a : b`）、`gate.if` |
比较 | `compare.ge` |
算术 | `math.add` |
事件 | `event.get`（字段：text/sender/channel/adapter/kind/mentions/isReply/received/dedupe）、`event.mention` |
集合 | `list.first` |
数据源 | `on.message`、`on.clock`、`read.const`、`read.variable` |
变量写 | `write.variable` |

**刻意不给** `filter` / `map` / `reduce` / 用户自定义函数——它们需要 lambda，等于造一门语言。

---

## 6. 三条不变量

1. **资源预算**：每 tick / 每 trace 调用预算。防的不是越权，是振荡与刷屏（`clock.tick` 连到 `sink.send` 就够刷爆）。**未实现**。
2. **权限边界**：`wire.apply` 不能改 caps、不能改治理配置，只有人能。**未实现**（`wire.*` 整体后置）。
3. **审计**：每次接线变更留 diff + 版本 + 可回滚。**未实现**。

关于数据敏感度：**不设 trust 分级**（D64）。接口固定让数据流天然显式——数据只能沿画出来的连线走，没有"读取任意上下文"的口子，因此不需要标签系统或污点分析；人 review 接线即可。成本落在"接线可读 + diff 可审"，不落在机制上。

---

## 7. 预制图

预制图装在**第③层**（激活决策），不是装在骨架里。装哪张由挂载表决定（`12-mounting-and-editor.md`）。

| 预制图 | 行为 | 状态 |
|---|---|---|
`preset.mention` | 群内被 @ 才响应 → `session.prompt` | **已实现**（沙箱默认） |
`preset.activation` | @ 立即激活 + 普通消息累积 20 条激活 + 激活后清零（旧 `RuntimeSession` 规则的图化版本） | **已实现**，与旧规则逐条对照通过 |
`preset.quiet` | 消息已入库后不激活会话、不回复（`on.message` 无汇块） | 已实现
`preset.digest` | 攒够 N 条或 T 分钟后总结 | 待做（需要 `sink.pulse`，见 §8） |
`preset.router` | 由图指定会话类型；消息优先投递到频道活跃会话，无活跃会话时按类型创建并投递 | 已实现

默认装 `preset.mention`——否则首启"没反应"会被误判成坏了。

---

## 8. 一图一激活源（不放开）

多激活源只有在"两个源给的参数一样"时才省事；参数不同就是负担。要组合就加**中间层图**：

```
消息图 ─┐
        ├─► sink.pulse ─► 摘要图（唯一激活源）
定时图 ─┘
```

因此 `sink.pulse` 是 `preset.digest` 的前置条件，**不是可选后置项**。

---

## 9. 现状与缺口

| 项 | 状态 |
|---|---|
事件模型（`InboundEvent`） | ✅ 已实现（`Contracts/InboundEvent.cs`） |
意图模型（`ActionIntent` + `IntentCatalog`） | ✅ 已实现（`Intent.cs`） |
`on.message` / `event.get` / `event.mention` / `sink.*` | ✅ 已实现 |
动态图（时钟沿 + 快照 + 上升沿） | ✅ 已实现，归档保留（`preset.digest` 会用） |
骨架（adapter / ingest / store / session / outbound） | ❌ 未实现 |
挂载表与实例池 | ❌ 未实现 |
`wire.describe / validate / apply` | ❌ 未实现（L-B 后置） |
`caps` 实际校验、预算、审计 | ❌ 未实现 |
`sink.pulse` | ❌ 未实现 |

---

## 10. 首个真实切片：激活判定（新 ↔ 旧对照）

**拆的是哪块**：`AgentCoreProcessor/Engine/Session/RuntimeSession.cs` 的 `WaitForActivationAsync`（约 1359–1411 行）——"这条消息要不要激活会话"。

| 旧规则 | 图里怎么表达 |
|---|---|
① 非消息端口（系统/定时/信号）有数据 → 立即 | 不属于本切片（需要 `on.timer` / `on.signal`） |
② mention 端口有数据 → 立即激活 | `event.mention` → `logic.or` |
③ normal 端口累积权重 ≥ 100（Weight=5 → 20 条）→ 激活 | `read.variable` + `math.add` + `compare.ge` |
激活后两个端口都清零 | `logic.select(any, 0, pending+1)` → 唯一写入点 |

对应的预制图：`preset.activation`（静态图 + 变量 `pending`）。

**对照方式**：旧逻辑抽成纯函数 `LegacyActivationPolicy.Decide(...)`（`Sandbox/Legacy/`），与新图放在同一条消息序列上逐条比对，并断言具体触发位置（第 20 条、mention 立即、mention 后重置）。

**这个切片暴露的两条设计约束**（已固化为规则）：

1. **同一个变量只能有一个写入点。** 最初想写"条件写"（达标时写 0、否则写 count+1），结果是同一变量写两次、后者覆盖前者，行为依赖求值顺序——脆弱。正确做法是把"条件"变成**值**：`next = cond ? 0 : count+1`，然后只写一次。为此引入了 `logic.select`（纯函数，不惰性求值，不构成控制流）。
2. **`write.variable` 不做条件写。** 它没有使能口；要"条件写"就在上游用 `logic.select` 算出值，而不是给汇块加使能口——否则汇块会慢慢长出控制流。

**结论**：这一块能行。代价是加了两个算子（`logic.or`、`logic.select`），收益是这条逻辑从"轮询端口队列 + 重置计数"变成了**可读、可 diff、可换**的一张图。

---

## 11. 真实链路验证（2026-10-25）

用 FileAdapter + 临时 Storage 实测，全程未读写正式 Storage。

| 场景 | 图 | 结果 |
|---|---|---|
固定回复 | `preset.keyword-test`（`sink.send`） | input「这是一次测试消息」→ `output/*.txt`「这是测试回复」✅ |
门控 + 模型回复 | `preset.keyword-agent`（`sink.prompt`） | input「测试：请用 speak 回复…」→ express 会话 → deepseek-v4-flash → speak → `output/001_file_*.txt`「你好 我是 Lilara」✅ |
不命中 | 同上 | 图放行=false → 跳过会话路径，不产生回复 ✅ |

**完整链路**（已实测连通）：

```
文件 → FileAdapter → AdapterManager.OnIncomingMessage
  → MasterEngine.RouteMessageAsync
  → ExpressEngineSpawnCheck.HandleMessageAsync
      ① Session.OnMessageAsync（入库）
      ② 权限门 → 伴随提取入批（骨架，与是否应答无关）
      ③ 图：preset.keyword-agent → SessionPrompt / SendMessage（图只产出意图，无返回值）
      ④ 骨架执行意图：SendMessage → AdapterManager.SendMessageAsync → IAdapter
      ⑤ 挂了图的频道由图接管，不再走默认会话路径
      ⑥ 会话 → AgentCore → 模型 → speak 工具 → 同上出向
```

**踩过的两个坑（都不是图的问题，是“看着像入口、实际是死路”）**：

1. 适配器消息**不走** `MessageEngine.AcceptIncoming`（那条只有 WebUI 注入在用）；真实入站咽喉是 `ExpressEngineSpawnCheck.HandleMessageAsync`。
2. `MessageEngine.OnOutgoingReady` 全仓**无人订阅**，`AdapterEngine.RegisterAdapter` 全仓**无人调用**；真实出向出口是 `AdapterManager.SendMessageAsync(platform, OutgoingMessage)`。

**因此定一条纪律**：接新动作（会话、工具、发消息）之前，先确认真实调用点，不要按名字猜。

**当前开关**：`LILARA_GRAPH_MODE=active` 才让图门控/派发；否则 shadow 只记日志。图由 `Storage/Graphs/mounts.json` 按频道挂载（见 §13）。

---

## 12. 下一阶段（建议顺序）

| 阶段 | 内容 | 为什么先做 |
|---|---|---|
N1 | **挂载表**：`GraphMount(Scope, ScopeKey, ClassId, Enabled)` + `GraphManager` | ✅ **已落地**（§13） |
N2 | **预制图体系**：`preset.quiet` 与 `preset.router`（先复用频道活跃会话，否则按图指定的会话类型创建并投递） | 验证挂载表选择的图能控制消息归属；会话类型档案提供默认 Core 与权限配置 |
N3 | **`wire.describe / validate / apply`** | agent 自改接线的前提；有挂载表才谈得上“改哪个实例” |
N4 | `sink.pulse` + `preset.digest` | 攒批/定时聚合；中间层图的前提（D65） |
N5 | 骨架（store/ingest/outbound 台账） | 与图并行的另一条线，独立价值 |

**暂不做**：画布编辑器（等 N3 有真实修改需求）；自创 DSL（D62）；ALC 合并（另一条线，由 SessionBrain/AlcBridge 推进）。

---

## 13. N1 挂载表（已落地）

```text
Storage/Graphs/
├── mounts.json        ← 挂载表（数据，可编辑）
└── *.json             ← 图本体（缺失时回退到程序目录 Graphs/，即随构建发布的默认图）
```

```json
{ "version": 1,
  "mounts": [
    { "scope": "channel", "scopeKey": "1", "classId": "preset.activation", "instanceName": "gate",  "order": 10, "enabled": true },
    { "scope": "channel", "scopeKey": "1", "classId": "preset.router",     "instanceName": "route", "order": 20, "enabled": true,
      "params": { "sessionType": "channel" } },
    { "scope": "global",  "classId": "preset.quiet", "enabled": false }
  ] }
```

**同一个作用域可以挂多张图。** 每张图各自求值、各自待命，互不读取彼此结论——
`order` 只决定跑图次序（小的先跑，相同则按声明顺序），不影响语义。
同一作用域内 `classId + instanceName` 必须唯一；重名（且都没写 `instanceName`）时后者被丢弃并记 `graph.mount.duplicate_instance`。

| 规则 | 行为 |
|---|---|
没有 `mounts.json` / 没有启用的挂载 | `GraphManager.TryCreate` 返回 null，**完全不干预消息链** |
频道未挂载 | `For(channelId)` 返回 null → 老行为（消息照常进会话） |
频道挂载且 `LILARA_GRAPH_MODE=active` | 图跑完就待命：骨架执行它产出的意图，本次消息**不再走默认会话路径** |
频道挂载但非 active（shadow） | 只记录 `graph.keyword.evaluated`，行为不变（继续走默认路径） |
同一消息多个投递意图 | 骨架按 `频道:消息id` 去重，只投递一次 |
图文件缺失 / 加载失败 | 只告警 `graph.mount.class_missing`，跳过该挂载 |

**图没有返回值。** `KeywordGraphRunner.EvaluateAsync` 只把本次求解产出的意图返回给调用方，
调用方不据此决定"放不放行"——放行与否由骨架的"挂载即接管"规则决定。
意图取自本次求解的局部结果，不读 `GraphInstance.LastIntents`，
因此不依赖"读的时候还没被下一拍覆盖"这种时序前提。

**三场景实测**（临时 Storage + 正式 DeepSeek 配置，未读写正式 Storage）：

| 场景 | 挂载 | 消息 | 结果 |
|---|---|---|---|
A | channel 1 | 不含关键词 | **0 回复、模型调用 0 次**（图拦下，连 token 都不花） |
B | channel 1 | 含「测试」 | 1 回复「挂载表生效」，模型调用 2 次 |
C | channel 999 | 不含关键词 | 模型调用增加到 4 次、有回复（channel 1 未挂载 → 不干预，走老行为） |

场景 A 与 C 的对比就是挂载表的核心价值：**同一进程里，挂了图的频道受图约束，没挂的频道行为完全不变**。

**实例隔离（已落地）**

| 项 | 行为 |
|---|---|
实例粒度 | 每个挂载点一个 `GraphInstance`，键 `classId@scope#scopeKey#instanceName`；同频道挂两张同类图靠 `instanceName` 区分 |
变量隔离 | 各实例变量独立（实测：channel 1 累积 5 条、channel 2 仍为 0） |
实例生命周期 | 跟随挂载记录：`mounts.json` 变更（时间戳变化）→ 重建挂载并丢弃全部实例；不再挂载的频道 `For()` 返回 null |
显式销毁 | `GraphManager.Release(channelId)` 销毁该挂载点实例与变量（供频道释放/运维调用） |
图类缓存 | `GraphClass` 按 classId 缓存（模板），只有实例按挂载点复制 |
