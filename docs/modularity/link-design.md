# 功能主体与连接：让内置数据流和自定义网络走同一个方案

> 设计提案，未实现、未编译。
> 目标：把「功能主体」和「外部连接」分开，并把**内置连接**与**用户自定义网络**统一到同一份结构、同一个加载器、同一个执行器。

---

## 0. 一句话

**功能主体 = 图上的节点**（私有状态 + 只暴露端口 + 不认识对面是谁）；
**连接 = `Link`**，内置连接和自定义网络是同一份 `LinkSpec` 的两个**来源**，不是两套机制。
唯一区分它们的是一个 `builtin: true` 字段 —— 它只影响 UI 可否编辑，不影响运行时。

---

## 1. 三条耦合的实证

这三条不是"感觉紧"，是能指到行的：

| 耦合 | 证据 | 本质 |
|---|---|---|
| **channel ↔ session** | `SessionEngine.cs:165,1214` 直接 `new ChannelContextService(dataSource)`；`SessionEngine.cs:688` 持有 `ClaimChannel`；反向 `Tool/Host/ChannelAccessImpl.cs:30` 用 `OfType<SessionEngine>()` 回取会话引擎 | **双向持有 + 共享可变状态**：频道占用的真相分散在 `SessionEngine._claim`、`DB/Channel`、`ChannelState/*.json` 三处 |
| **channel ↔ model** | `Engine/Context/ChannelContextService.cs:242,258,332,367,433` 直接构造/产出 `Message`、`ContentPart`（LLM 协议类型）；`ChannelContextService.cs:134` 读 `VisionEngineConfig.OcrRichTextThreshold` 决定 OCR | **频道域替模型层做决定**：领域片段与 LLM 消息格式混在一个类里 |
| **channel ↔ adapter** | `Engine/Context/ChannelContextService.cs:8`、`Tool/Host/ChannelAccessImpl.cs:6` 均 `using AgentCoreProcessor.Adapter`；`Engine/AdapterEng/AdapterEngine.cs:60` 用 `IChannelAwareAdapter.OwnsChannel` 反查该由谁发 | **出向投递靠"频道归谁"反查**：适配器被迫知道频道语义，频道被迫知道适配器清单 |
| **channel ↔ 存储实体** | `Engine/Context/IChannelDataSource.cs` 直接暴露 `UserMessage` / `User` / `Person`（DB 实体） | 频道域与表结构同构，换存储要改领域代码 |

一句话概括：**频道现在同时是"领域对象""会话的资源""模型上下文的拼装者""适配器的路由依据""表结构的镜像"。**

---

## 2. 判据：什么算功能主体，什么算外部连接

| | 功能主体（Core） | 外部连接（Edge） |
|---|---|---|
| 有状态 | 是，且状态**私有** | 否（只持有连接参数） |
| 做业务判断 | 是 | 否，只做形状翻译 |
| 认识对面是谁 | **不认识** | 只认识协议 |
| 删掉它的后果 | 业务规则变了 | 只是"接不上了"，规则不变 |
| 例子 | 频道占用判定、路由决策、上下文取舍 | OneBot 收发、HTTP bridge、文件落盘 |

这条判据能直接回答"某段代码该放哪"：**如果删掉它业务规则要改，它就是主体；否则是连接。**

---

## 3. 已有资产：不需要另起一套

这是本设计能落地的关键 —— 项目里已经有一整套图运行时，缺的只是**把内置路径也交给它**。

| 现有类型 | 已解决的问题 | 在本设计里的角色 |
|---|---|---|
| `Link(FromNode, FromPort, ToNode, ToPort)`<br>`GraphRuntime/Graph/Model.cs:47` | 连线的最小表示 | **就是连接** |
| `Cell`（Unknown / NoEvent / Known）<br>`Graph/Model.cs` | 端口上的值，含"本拍无事件" | **连接上流动的东西** |
| `INode.Evaluate(inputs, ctx) → outputs`<br>`Graph/Model.cs` | 主体的执行契约，无副作用声明 | **功能主体的接口** |
| `GraphClass` = nodes + links + variables | 一张图 = 一个可装配单元 | **一个装配单元** |
| `GraphInstance.Run`：失败零副作用<br>`Graph/GraphInstance.cs` | 拍的原子性 | **拍的语义** |
| `InboundEvent`<br>`GraphRuntime/Contracts/InboundEvent.cs` | 外部世界 → 内部的唯一信封，按产生者主体分类，`External` 无频道 | **入边信封** |
| `ActionIntent` + `IntentCatalog`<br>`Graph/Intent.cs` | **图不执行动作，只产出意图**，由骨架执行 | **出边信封**（主体不直接调对方） |
| `ModuleAttribute` / `ModuleHost.Bind`<br>`Modules/` | 反射生成描述符 + 显式绑定实现，没绑定就加载失败 | **主体如何变成节点** |
| `ISinkExecutor` / `IIntentSink`<br>`Graph/SinkExecution.cs` | 即时汇 / 派发汇 | 出边的两种形态 |
| `IInputPort` / `IOutputPort` / `PortSpec`<br>`PluginSDK/` | 旧端口抽象（订阅频道 + 权重） | **与图模型重叠**，见 §4.4 的处理 |

**关键洞察**：`ActionIntent` 这套"图不执行动作、只产出意图"的设计，正好就是"功能主体不调用对方"的落地形式。所以断耦合不需要发明新范式，只需要**把现在绕过图的直连补进图里**。

---

## 4. 连接模型：只加两样东西

### 4.1 `LinkSpec`

```csharp
public sealed record LinkSpec(
    string From,      string FromPort,
    string To,        string ToPort,
    LinkPolicy Policy,
    bool Builtin);    // 唯一区分内置/自定义的字段：只影响 UI 可编辑性
```

内置连接与自定义连接走**同一个 `GraphLoader`、同一个 `Solver`、同一个 `GraphInstance`**。这是"同一个方案"的可验证形式：同一份加载代码能吃两份文件。

### 4.2 `LinkPolicy`

```csharp
public sealed record LinkPolicy(
    DeliveryMode Mode,          // Inline（拍内同步，默认）| Deferred（排队到下一拍）
    int Capacity,               // 仅 Deferred 有意义；0 = 无界
    OverflowPolicy Overflow,    // Block | DropOldest | DropNewest | Fail
    int ActivationWeight,       // 承接现有 IInputPort.Weight 的语义（100 立即 / 5 攒多了）
    string? Guard,              // 可选谓词节点类型；为假则不投递
    int MaxHops);               // 防环
```

`ActivationWeight` 是必须补的 —— 现有 `IInputPort.Weight`（100=立即激活，5=攒多了激活）在图模型里没有对应物，直接丢掉会改变行为。

### 4.3 拍内连接一律 Inline

这是本设计最关键的取舍，理由：

`GraphInstance.Run` 的语义已经是 **「一次激活 = 一次求解，失败零副作用」**。异步不该出现在连接上，只该出现在**图的边缘**。所以：

| 连接类别 | 两端 | 模式 | 例子 |
|---|---|---|---|
| **拍内连接** | 主体 → 主体 | `Inline`（同步，失败整拍失败） | `channel.route → session.actor` |
| **入边** | 外部 → 主体 | `Deferred` + 有界队列 | `adapter.onebot → channel.inbox` |
| **出边** | 主体 → 外部 | `Deferred` + 有界队列 | `session.reply → outbound.router` |

三类**共用 `LinkSpec`**，只有 `Policy` 不同。这样内置路径不需要调度器就能跑，`Deferred` 只在两个边缘实现，工作量可控。

### 4.4 与 `IInputPort` / `IOutputPort` 的关系

`PortSpec{ChannelId, SubStream, Weight}` 表达的是"订阅某频道某子流"，`Weight` 表达激活优先级。在图模型里：

- `ChannelId/SubStream` → 一条 `Link` 的 `Guard`（谓词节点）+ `InboundEvent.ChannelKey`
- `Weight` → `LinkPolicy.ActivationWeight`

**建议**：`IInputPort`/`IOutputPort` 保留给**插件作者**（`ITool` 通过它们写数据，SDK 兼容不动），但**内部一律编译成 `LinkSpec`**，不再有两套投递实现。这样插件不受影响，内部只有一套语义。

---

## 5. 三条耦合的具体断法

### 5.1 channel ↔ session：占用从"跨模块锁"变成"节点的私有变量"

**Before**（现状）
```
SessionEngine ──new──▶ ChannelContextService ──▶ SessionAgentHost
      │
      └── ClaimChannel/ReleaseChannel（会话池里改占用表）
RuntimeSession ──▶ _sessionEngine.ClaimChannel(...)
ChannelAccessImpl ──OfType<SessionEngine>()──▶ 反查占用
```

**After**（图）
```
source.adapter ──InboundEvent──▶ channel.inbox
channel.route ──RouteDecision{channelKey, sessionType, weight}──▶ session.actor
session.actor ──AcquireRequest{channelKey}──▶ channel.occupancy
channel.occupancy ──Grant/Deny──▶ session.actor
session.actor ──ActionIntent.SendMessage──▶ outbound.router
```

变化要点：

- **占用（claim）成为 `channel.occupancy` 节点的私有变量**，只经两个显式端口对外开放：入 `acquire` / `release`，出 `granted` / `snapshot`。
  → 这正好修掉先前发现的「三处状态分离」：**唯一权威 = 节点变量**，`DB/Channel` 与 `ChannelState/*.json` 都退化为它的持久化后端（经 `IChannelStore` 端口）。
- `session.actor` 不认识频道，只认识自己入端口上的 `RouteDecision` / `Grant`。`SessionEngine` 不再 `new` 任何频道服务。
- `ChannelAccessImpl` 的 409 行里，凡 `OfType<SessionEngine>()` 的部分改成读 `channel.occupancy` 的输出口，不再回取会话引擎。

**这一条与 C3（断环）、C12（channel 归位）是同一件事的三个面，必须一起设计。**

### 5.2 channel ↔ model：频道只产出领域片段，不产出 LLM 消息

**Before**：`ChannelContextService` 直接产出 `List<Message>` / `ContentPart`，还顺手决定 OCR 阈值。

**After**：定义纯领域 DTO

```csharp
public sealed record ChannelContextFragment(
    string ChannelKey,
    ChannelKind Kind,
    IReadOnlyList<ParticipantView> Participants,
    IReadOnlyList<MessageRef> RecentMessages,   // 只含 id / 平台 id / 发送者 / 时间 / 是否有图
    IReadOnlyList<QuotedRef> QuotedChain,
    float Affinity,
    string? Memo,
    IReadOnlyList<ImageRef> Images);            // 只标记"这里有图"，不决定要不要 OCR
```

- 拼 `Message` / `ContentPart` → 移入 `Lilara.Context`（它本就有 `ILlmPort`）
- OCR 决策 → 移入 `Lilara.Vision`（`ChannelContextService.cs:134` 那处 `VisionEngineConfig` 依赖消失）
- 存储实体 → `IChannelDataSource` 改为返回 `MessageRef` / `ParticipantView` 等 DTO，不再暴露 `UserMessage/User/Person`

**可断言的收益**：`Lilara.Channel` 的源码里 `using AgentCoreProcessor.Models` 与 `using ...Engine.Vision` 归零，`IChannelDataSource` 不出现 `Database` 命名空间。这三条可以写成 CI 检查。

### 5.3 channel ↔ adapter：删掉 `IChannelAwareAdapter`

**Before**：`AdapterEngine.ResolveAdapter` 遍历适配器问 `OwnsChannel(channelId)`。

**After**：
- 入向：adapter 只产出 `InboundEvent{AdapterId, ChannelKey}`（已有），不参与任何频道判断
- 出向：`ActionIntent.SendMessage(Target=ChannelKey)` 交给 `outbound.router` 节点，按 `AdapterId` 前缀（`onebot:*`）或订阅记录确定适配器
- **删除 `Adapter/IChannelAwareAdapter.cs`（12 行）**，`AdapterEngine` 不再认识频道语义

结果：`adapter` 不知道频道，`channel` 不知道适配器，中间只有 `InboundEvent` 与 `SendMessage` 两种信封。

---

## 6. 端口与信封清单

| 节点 | 入端口 | 出端口 | 私有状态 |
|---|---|---|---|
| `channel.inbox` | — | `event: InboundEvent` | 去重窗口（DedupeKey 环形缓冲） |
| `channel.route` | `event: InboundEvent`、`occupancy: OccupancySnapshot` | `route: RouteDecision` | 路由规则（内置 + 用户可覆盖） |
| `channel.occupancy` | `acquire: AcquireRequest`、`release: ReleaseRequest`、`tick: ClockTick` | `grant: Grant`、`snapshot: OccupancySnapshot` | **频道 → 占用者 / 强度 / 期限**（唯一权威） |
| `channel.context` | `channelKey`、`limit: int` | `fragment: ChannelContextFragment` | 无（纯查询） |
| `channel.state` | `set: StatePatch` | `value: Cell` | 亲和度 / 备注 / 配置（经 `IChannelStore` 持久化） |
| `session.actor` | `route: RouteDecision`、`grant: Grant`、`input: InputData`、`mailbox: string` | `acquire: AcquireRequest`、`intent: ActionIntent`、`turn: TurnResult` | 会话私有上下文（原 `RuntimeSession` 状态） |
| `adapter.*` | — | `event: InboundEvent` | 连接状态 |
| `outbound.router` | `intent: ActionIntent` | —（汇） | 适配器订阅表 |

**信封只有三种**：`InboundEvent`（进）、`ActionIntent`（出）、领域 `record`（拍内）。三者都不含宿主实例引用 —— 这是"可序列化"的硬约束，也是 webui 独立进程后能复用的前提（见 `tree-cut.md` C13）。

---

## 7. 内置连接清单（一张真实的内置图）

内置图以**与用户图完全相同的 JSON 格式**随程序发布，放在 `graphs/builtin/ingress.json`：

```json
{
  "classId": "builtin.ingress",
  "kind": "Static",
  "nodes": [
    { "id": "in",        "type": "module.channel.inbox" },
    { "id": "route",     "type": "module.channel.route" },
    { "id": "occupancy", "type": "module.channel.occupancy" },
    { "id": "ctx",       "type": "module.channel.context" },
    { "id": "session",   "type": "module.session.actor" },
    { "id": "out",       "type": "sink.send" }
  ],
  "links": [
    { "from": "in",        "fromPort": "event",    "to": "route",     "toPort": "event",     "builtin": true },
    { "from": "route",     "fromPort": "route",    "to": "session",   "toPort": "route",     "builtin": true },
    { "from": "session",   "fromPort": "acquire",  "to": "occupancy", "toPort": "acquire",   "builtin": true },
    { "from": "occupancy", "fromPort": "grant",    "to": "session",   "toPort": "grant",     "builtin": true },
    { "from": "session",   "fromPort": "intent",   "to": "out",       "toPort": "intent",    "builtin": true },
    { "from": "route",     "fromPort": "route",    "to": "ctx",       "toPort": "channelKey","builtin": true },
    { "from": "ctx",       "fromPort": "fragment", "to": "session",   "toPort": "input",     "builtin": true }
  ]
}
```

**这就是"同一个方案"的证明**：这份文件与用户在 WebUI 里画出来的图结构一致，`builtin: true` 只是让 UI 把它渲染成只读。用户可以另存一份、改一条连接、注入自己的过滤节点，走的是同一套加载与求解。

---

## 8. 执行语义

| 维度 | 规则 |
|---|---|
| **一拍的定义** | 一个 `InboundEvent` 触发一次 `GraphInstance.Run` |
| **拍内** | `Inline` 连接，深度优先求解，`Cell` 三态（Unknown / NoEvent / Known） |
| **失败** | 整拍零副作用：不提交变量、不产出 intent、不触发汇（现状语义，保持） |
| **拍间** | 入边/出边是有界队列，满时按 `OverflowPolicy` |
| **顺序** | **同一 `ChannelKey` 的拍严格串行**（保证频道内因果）；不同 `ChannelKey` 可并行 |
| **防环** | `LinkPolicy.MaxHops` + 现有 `InboundEvent.Depth`（错误事件只多走一跳） |
| **背压** | 只在边缘：队列满 → 上游 `Deferred` 写入阻塞或丢弃，绝不在拍内阻塞 |
| **超时** | 拍内不设超时（同步求解）；边缘设超时（一次投递 / 一次发送） |

**为什么拍内不同步取消**：`Solver` 是纯函数式求解，`Evaluate` 里如果有 IO（如 `channel.context` 查历史），要么改成"查好再进拍"（推荐），要么就得引入异步求解器。本设计选前者：**IO 发生在拍之前（数据装载）或拍之后（汇执行），拍内保持纯求解**。

---

## 9. 与现状的映射：保留 / 改造 / 删除

| 现状 | 处置 | 说明 |
|---|---|---|
| `GraphRuntime` 全套（Model/Cell/Solver/GraphInstance/ModuleHost/Intent） | **保留** | 本设计直接复用，不新增运行时 |
| `InboundEvent` / `ActionIntent` / `IntentCatalog` | **保留** | 已是正确形状 |
| `Engine/Context/ChannelContextService.cs`（521 行） | **拆** | 领域片段生成 → `Lilara.Channel`；LLM 消息拼装 → `Lilara.Context`；OCR 决策 → `Lilara.Vision` |
| `Engine/Context/IChannelDataSource.cs` | **改造** | 返回值从 DB 实体换成 DTO |
| `Engine/Worker/ChannelStateManager.cs` | **改造** | static → `IChannelStore` 端口实现；json 与 DB 的权威问题见 Q5 |
| `SessionEngine.ClaimChannel/SubscribeChannel/ReleaseChannel` | **移出** | → `channel.occupancy` 节点变量 |
| `Engine/Session/ConnectedChannelState.cs` | **删除** | 并入 `channel.occupancy` 的 `snapshot` 输出 |
| `Adapter/IChannelAwareAdapter.cs` | **删除** | 出向改由 `outbound.router` 决定 |
| `Engine/Tool/Host/ChannelAccessImpl.cs`（409 行） | **改造** | `OfType<SessionEngine>()` 部分改读 `occupancy` 输出口 |
| `IInputPort` / `IOutputPort` / `PortSpec` | **保留但内部编译成 LinkSpec** | 插件 API 不动，内部只留一套语义 |
| `Engine/Worker/ILoopBus.cs` + `ModuleBus` / `EventBus` | **待判** | 与图连接职责重叠，见 §11 Q8 |

---

## 10. 迁移顺序

前置：`tree-cut.md` 的 `C1`（拆 csproj）已完成，否则下面都在一个编译单元里做，无法验证边界。

| 阶段 | 做什么 | 为什么这个顺序 | 风险 |
|---|---|---|---|
| **L1** | **影子图对拍**：把现有 adapter→channel→session 路径用图表达一份，跑双份并比对输出 | `AlcBridge` 里已有 `WireComparer` / `CanonicalComparer`，可复用做对拍；不动生产路径 | 低 |
| **L2** | 断 channel ↔ model（领域片段 + OCR 决策外移） | 最容易、收益明确、**可写成 CI 断言** | 低 |
| **L3** | 断 channel ↔ adapter（删 `IChannelAwareAdapter`） | 只动 12 行 + 路由逻辑，独立可测 | 低 |
| **L4** | 断 channel ↔ session（占用收进 `channel.occupancy`） | 最难，与 `C3`（断环）、`C12`（channel 归位）耦合 | **高** |
| **L5** | 内置图切到 JSON 加载，与用户图同源 | 此时才真正"同一个方案" | 中 |

L1 的产出直接决定 L4 可行不可行 —— 对拍不过就说明图表达漏了语义。

---

## 11. 未验证与待定

| id | 问题 | 现状 |
|---|---|---|
| Q8 | `ILoopBus` / `ModuleBus` / `EventBus`（28 处引用）与图连接职责重叠，该合并还是并存？ | 未追调用链，只知 `ILoopBus` 在 `Engine/Worker/` |
| Q9 | `ILoopComponent` 的生命周期钩子（`OnActivated`/`OnPause`/`OnBeforeInvoke`/`OnAfterInvoke`）怎么映射到图的"无状态求解"？ | 倾向：**主体实现为模块实例（有状态），节点是它的适配器**，钩子挂在 `LifecycleAttribute` 的 mount/kill 上；但要验证 `ModuleHost` 是否够用 |
| Q10 | `channel.context` 拍内查历史是 IO —— 按本设计应"拍前装载"，但现有调用点在 `SessionAgentHost.cs:67-88` 是懒取 | 未设计装载点的触发条件 |
| Q11 | `ActionIntent.Notify` 的语义（图不知道会话 id，由订阅记录决定）与 `SendMessage(Target=ChannelKey)` 是否会冲突 | 需读骨架侧消费代码 |

**未验证声明**：本文全部基于源码静态阅读，未 build、未 run、未 test。`Solver` 的求解顺序、`Cell` 三态在真实链路里的表现、`Inline` 连接是否真能满足现有全部数据流，都只有跑过才算数。特别是**「拍内一律 Inline」这个核心取舍，需要 L1 对拍来证伪**。
