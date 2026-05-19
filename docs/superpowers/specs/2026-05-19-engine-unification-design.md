# 引擎循环统一设计规范

> 状态：整体设计定稿。16 章节全部敲定。可进入实施计划阶段。

## 一、分层定义

| 层级 | 定义 | 是否必须 | 现有对应 |
|------|------|---------|---------|
| **ISubEngine** | 独立生命周期。`RunAsync()`、`Status`（Running/Idle/Stopping）、CTS。特殊状态（Sleeping）由具体引擎暴露属性，不进接口。 | 所有引擎 | ChannelEngine, SystemEngine, DreamEngine, TaskEngine, VisionEngine, TimerEngine |
| **Gate** | 框架提供的 concrete 基类。统一的循环骨架：等触发→判条件→跑执行→决继续。引擎 override 钩子，不自己写 while。 | 有循环的引擎 | 各引擎 RunAsync 的 while 块 |
| **Agent** | 可复用的 concrete class。封装"构建上下文→调模型→执行工具→是否继续"的多轮推理循环。随 Engine 同生共死，不可独立寻址。 | 需要多轮推理的引擎 | 裸写在 SystemEngine.RunAgentLoopAsync / ChannelEngine 内层循环中 |
| **Core** | 纯模型调用。输入 messages → 输出 ModelOutput。无状态、无循环。 | 需要调模型的引擎 | AgentCore |
| **Module** | 循环内注入点。感知总线事件，产出 prompt 片段。依附于引擎。 | 需要动态注入的引擎 | SpeakModule, LoopControlModule |
| **Fragment** | 做梦时的独立分析单元。可选装 Core 或 Agent，执行完毕产出结果。 | DreamEngine | MemoryConsolidation, ContextReview, CodeReview |

**引擎 = Gate（自己装配钩子）+ 可选 Agent + 可选 Core + 若干 Module + 若干 Fragment（Dream 专属）。**

## 二、信号埋点规范

信号日志系统（`SignalContext` + Signal DB）贯穿所有层级。核心原则：**框架层自动埋标准 span，引擎层不操心底层，只管业务 span。**

### 2.1 骨架层级（框架自动产出）

所有装配 Agent 的引擎，框架自动产出以下 span，引擎一行不写：

```
[gate:activate]           ← ShouldActivate 通过即开
  └─ [agent:loop]         ← 一次 Gate 执行周期的完整 Agent 循环
      ├─ [agent:round]    ← 每轮（可折叠，默认不展开）
      │   ├─ [core:invoke] ← 模型调用（token数、缓存命中率）
      │   └─ [agent:tools] ← 有工具调用时开
      │       └─ [tool:xxx] ← 单工具执行
      └─ [agent:stop]     ← 停止原因 + 汇总（总token、总轮数）
```

不装 Agent 的引擎（Dream/Vision/Timer），框架只埋 `[gate:activate]`，其余由引擎自行埋。

### 2.2 信号传播规则

| 场景 | 方式 | 说明 |
|------|------|------|
| EventBus 事件唤醒 Gate | `Signal.Continue(event.TraceSignalId, event.TraceParentSpanId)` | 接上游因果链，日志页能画跨引擎连线 |
| Gate 内部 Signal()/超时唤醒 | `Signal.Begin()` | 自主唤醒，新开信号 |
| Agent 内串行执行 | AsyncLocal 自然嵌套 | `agent:round` 自动挂在 `agent:loop` 下 |
| 子 Agent / Fragment 并行 | `Signal.Continue` 独立上下文 | `CauseSpanId` 指向父 agent span |

### 2.3 引擎业务 span

引擎在框架骨架基础上自由追加自己的 span：

- **ChannelEngine**：缓冲窗口计时、前缀组装、压缩执行
- **SystemEngine**：自检评估、委托决策、睡觉评估
- **DreamEngine**：Fragment 调度、资源预算计算

业务 span 通过 `Signal.Open()` 挂在当前骨架 span 下，自动获得正确的父子关系。

### 2.4 两层体系共存

信号日志（Signal DB）和模型日志（ModelLog DB）暂时分开：

| 体系 | 存储 | 内容 |
|------|------|------|
| Signal DB | 结构化 span/event，含 `core:invoke` 的 token 摘要 | 引擎行为追踪、因果链、跨引擎关联 |
| ModelLog DB | 每次模型调用的完整 usage | 按 Core/模型聚合统计、缓存命中率 |

`core:invoke` 同时写两者（Signal DB 带摘要，ModelLog DB 写完整记录），日志页查询时按需选源。

### 2.5 LogWriter 接口

```csharp
class LogWriter {
    void Enqueue(LogEvent e);        // 入队 span open + event
    void EnqueueClose(LogEvent e);   // 入队 span close（WAL 同步写入）
}
```

- 单线程写 + WAL 模式，不阻塞引擎
- 引擎启动时 `Signal.Init(writer, minLevel)`
- minLevel 可运行时调整（`Signal.SetMinLevel`）

## 三、ISubEngine 状态

```csharp
enum EngineStatus { Running, Idle, Stopping }
```

- 基础状态三个，全引擎通用
- Sleeping 是 SystemEngine/DreamEngine 专属概念，自己暴露 `CurrentSleepState` 属性
- 能力标记：`HasAgentLoop`、`HasModelCall`、`HasGate` — 布尔属性，非类型分类

## 四、Gate 规范化

### 4.1 职责边界

Gate 是框架提供的 concrete 基类，**只管放不放**（循环骨架），判断逻辑全给引擎。引擎 override 钩子，不自己写 while。

```csharp
abstract class Gate {
    // 等任一触发：总线事件 / 定时器 / Signal() / CTS
    protected async Task WaitForTriggerAsync(CancellationToken ct);

    // 引擎实现
    protected abstract Task<bool> ShouldActivate();
    protected abstract Task ExecuteAsync(CancellationToken ct);

    // 内部唤醒（引擎自己或回调调用，如 DreamEngine 片段完成）
    public void Signal();

    // 强制唤醒入口（跳过 ShouldActivate，直接开闸）
    public void ForceWake();

    // 循环骨架
    public async Task RunAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            await WaitForTriggerAsync(ct);
            if (!await ShouldActivate())
                continue;
            await ExecuteAsync(ct);
        }
    }
}
```

### 4.2 闸门行为

- **开闸即落闸**：触发→ShouldActivate 通过→执行。执行期间又有人敲→执行完立刻再开一次，不空等
- **双路径唤醒**：
  - EventBus 事件（MessageEvent 等）→ 唤醒 → ShouldActivate 评估 → 通过才开。ShouldActivate 返回 false 时事件留在 Engine 缓冲，不丢弃
  - `ForceWake()` → 直接开闸，跳过 ShouldActivate。用于委托完成、调试触发等场景
- **无 ShouldContinue**：有 Agent 的引擎，Agent 自己管停止；DreamEngine 等自定义循环自己管
- **缓冲窗口是 Engine 的事**：收到消息后 Engine 自己决定立刻敲闸门还是等缓冲窗口结束再敲，Gate 不关心

### 4.3 等待机制组合

`WaitForTriggerAsync` = EventBus 事件推送 + 内部 `Signal()` + 定时器超时 + CTS 取消，`WaitAny` 任一满足即返回。

Gate 构造时拿 `EventBus` 引用，订阅 `OnEvent`。WaitForTriggerAsync 内部监听 EventBus 事件，收到即唤醒。

### 4.4 引擎各自实现

| 引擎 | Gate | ExecuteAsync | 触发源 |
|------|------|-------------|--------|
| ChannelEngine | 有 | 跑 Agent | 新消息(缓冲窗口)、总线唤醒 |
| SystemEngine | 有 | 跑 Agent | SpawnCheck 定时器、新委托(EventBus)、总线唤醒 |
| DreamEngine | 有 | Fragment 调度循环 | SpawnCheck 定时器、片段完成回调(Signal)、总线唤醒 |
| TaskEngine | 可选 | 跑 Agent | 一次性运行，可复用 Gate 做多轮 |
| VisionEngine | 无 | — | 按需直接调 Core |
| TimerEngine | 无 | — | 按需直接调 Core |

ChannelEngine 和 SystemEngine 的 Agent 用法一致，差别仅在 BuildStartInjectAsync 产出内容不同（频道循环有新聊天消息，系统循环有委托摘要/任务/通知）。

## 五、Agent

### 5.1 定位

Agent 是 concrete class，随 Engine 同生共死，不可独立寻址。封装完整的多轮推理循环（小循环），Engine/Gate 管外层循环（大循环）。

- **Agent 管**：推理循环、工具执行、退避、对话历史、停止判断
- **Engine 管**：前缀/摘要管理、信号缓冲/格式化注入、压缩、持久化、Mode

不装 Agent 的引擎（Dream/Vision/Timer）直接用 Core 做单次调用。

### 5.2 循环模型

Agent 内部 while 循环，跑完整多轮推理直到停止条件触发。每轮三块拼装：

```
[prefix]        ← Engine 给一次，Agent 缓存
[summary]       ← Engine 给
[history]       ← Agent 自己记的 user/assistant 对

[注入块]        ← Engine 给本轮（新消息+模块+通知+指令），一条 user message
[工具结果块]    ← Agent 产，上轮工具执行结果（一条 user message）
```

三块拼好 → 调 Core → 产出 assistant → 可能带工具调用 → 执行工具 → 追加到 history → 下一轮。

### 5.3 信号通道：IAgentHost

Agent 不直接接收外部事件。Engine 用内部 `ConcurrentQueue<ChannelSignal>` 缓冲所有信号。Agent 通过两个时机拉取注入：

```csharp
interface IAgentHost {
    /// <summary>每次 Agent.RunAsync() 启动时调一次。一次性内容：新消息、委托变更、压缩产物等。</summary>
    Task<List<Message>> BuildStartInjectAsync();

    /// <summary>Agent 每轮调一次。持续内容：LoopControl 轮次提示、模式提醒等。</summary>
    Task<List<Message>> BuildRoundInjectAsync();
}
```

### 5.3.1 信号类型（ChannelSignal）

```csharp
abstract record ChannelSignal;
record NewMessageSignal(IncomingMessage Msg) : ChannelSignal;
record BusEventSignal(EngineEvent Event) : ChannelSignal;
record CompressionSignal(string Summary, List<Message> RetainedHistory) : ChannelSignal;
record ModeSwitchSignal(string NewMode) : ChannelSignal;
```

Engine drain 缓冲 → 遍历 → 每个信号类型自己知道怎么格式化进注入 Message。不需要 `object` 转类型。

- Engine 自己缓冲、自己格式化、自己分清一次性/持续
- Agent 不碰信号类型，只拿 `List<Message>`
- 图片等富内容通过 Message 的 ContentPart 自然携带，无需特殊处理
- 主动查询类（如详细记忆检索）走工具，不进注入层

插件/模块侧通过实现对应接口来注入：

```csharp
interface IInjectProvider {
    int InjectPriority { get; }             // 排序优先级，越小越靠前
    Task<string> BuildStartInjectAsync();   // null = 无内容
    Task<string> BuildRoundInjectAsync();   // null = 无内容
}
```

Engine 收集所有注册的 `IInjectProvider`，按 `InjectPriority` 升序排列后拼成注入 Message。与现有 `EngineModule.PromptPriority` 模式一致。

**`EngineModule` 直接实现 `IInjectProvider`**：`BuildStartInjectAsync` 映射到 `BuildPromptSection`，`BuildRoundInjectAsync` 返回 null。老模块自动兼容，无需适配层。

### 5.4 停止逻辑

| 条件 | 行为 |
|------|------|
| 模型本轮无工具调用 且 缓冲为空 | 真停止，返回 `Completed` |
| 模型本轮无工具调用 但 缓冲不为空 | 有新信息未评估 → 继续下一轮 |
| 调了 `wait` 工具 | 返回 `WaitRequested`，Engine 自行处理等待 |
| 达到 `MaxRounds` 硬上限 | 返回 `MaxRounds`，未处理信号留在缓冲里 Engine 决定下一步 |
| ForceStop 被调用 / CTS 取消 | 返回 `ForceStopped` / `Cancelled` |

### 5.5 Agent 公开状态

```csharp
class Agent {
    AgentStopReason StopReason { get; }
    int TotalInputTokens { get; }
    int TotalOutputTokens { get; }
    int TotalRounds { get; }
    List<Message> History { get; }       // 本次循环的对话历史
    bool IsInBackoff { get; }
    DateTime BackoffUntil { get; }
    List<object> UnprocessedSignals { get; }  // MaxRounds 停止后未处理的信号
}

enum AgentStopReason { Completed, MaxRounds, WaitRequested, CompressNeeded, ForceStopped, Cancelled, Error }
```

### 5.6 退避策略

Agent 内部持有 `_consecutiveFailures` 计数器，可配置退避数组（默认指数 `{10, 30, 60, 120, 300}` 秒）。成功时重置。退避期间 Engine 仍可往缓冲塞信号，Agent 退避结束继续循环。

### 5.7 Message 序列化

Agent 负责 Message → API JSON 的机械转换（`List<Message>` → 遍历 → 按 role 拼 prompts）。不关心 Message 语义，只做格式转换。所有引擎复用同一套序列化逻辑。

## 六、统一上下文结构

### 6.1 整体结构

```
system: [固定前缀]         ← 永不变，缓存锚点
   ├─ 身份/核心规则
   └─ 全部工具定义（不按模式裁剪，永远全量）

messages:
  [Agent.History]          ← 对话历史（user/assistant 交替），Agent 实例持有
    ├─ 冷启动时为空
    ├─ 每轮追加 user(注入块)/assistant(模型输出)/user(工具结果)/assistant(…)
    └─ 可能含摘要 signal 产生的历史片段（见 6.2）
  [注入块]                 ← BuildInjectAsync() 产出，一条 user message
  [工具结果块]             ← Agent 产出，上轮工具结果（一条 user message）
```

**前缀不可触碰。Mode 切换零缓存代价。** 对话历史中不保留工具调用/思考过程，只保留纯聊天文本。

### 6.2 注入块格式化

Agent 每轮 `_host.BuildInjectAsync()` → Engine drain 缓冲 → 按 slot 排序 → 拼成一条 user message：

```
<本轮注入>
  <系统指令>行为指令、模式说明</系统指令>       ← SystemDirective
  <通知>委托结果、系统通知</通知>              ← Notifications
  <新消息>                         ← NewMessages
    用户: 今天天气不错
    用户: 帮我查下文档
  </新消息>
  <模块产出>LoopControl 提示等</模块产出>       ← ModuleOutput
</本轮注入>
```

slot 顺序固定，缓存友好。每个 slot 可空，不影响整体结构。

### 6.3 压缩产物在上下文中的位置

压缩产物（`CompressionSignal`）在 `BuildStartInjectAsync` 被 drain，与其他信号一同格式化进注入块。新 Agent 在注入块开头看到摘要和保留对话，不打乱 user/assistant 交替。完整压缩流程见 §八。

## 七、Mode 定义

```csharp
string ContextMode  // "express" | "working" | "task" | "review" | "system"
```

Mode 只控制外围，不动前缀：

| 维度 | 控制什么 |
|------|---------|
| 模型 | express→小模型，working/review→聪明模型 |
| 压缩 | 不同 mode 不同阈值/参数 |
| 工具可用 | ToolRegistry 按 mode 开关，前缀里定义全在 |
| 行为指令 | 当前轮注入，不影响缓存 |

## 八、压缩策略

三层阶梯递进，模型有主动权。L1+L2 异步不阻塞，L3 硬保底。

### 8.1 三层模型

| 层 | 触发条件 | 注入内容 | 模型行为 |
|----|---------|---------|---------|
| L1（软提示） | tokens > 阈值1 | "上下文较长。如果当前话题不重要（如闲聊），可以调用 `compress` 工具压缩。例如：`compress` 会保留最近对话生成摘要。" 仅首次越界时注入一次。 | 模型自由选择，不处理也行 |
| L2（中提醒） | tokens > 阈值2 | 每轮注入"建议尽快调用 `compress` 压缩上下文，腾出空间。" | 模型应认真考虑 |
| L3（硬保底） | tokens > 阈值3 | "⚠ 这是压缩前最后一轮对话，本轮结束后将强制压缩。请简要告知用户。" | 本轮结束 → 同步阻塞压缩，无论模型是否调 `compress` |

阈值可配置，默认值按 mode 不同（express 阈值低，working 阈值高）。

### 8.2 compress 工具

- 全部模式可用（含 Express），Express 下原生 tool_use
- 模型调 `compress` → Engine 启动异步压缩 → 开独立 `Signal.Begin("compress")` scope
- 不调 → L1/L2 不强制，L3 强制同步压缩

### 8.3 异步压缩流程（L1/L2）

```
模型调 compress（或 Agent 返回 CompressNeeded）
  → Engine 启动后台压缩 Task（不 await）
  → Gate 继续，事件照常响应，新消息正常入缓冲

后台压缩完成
  → 打包 CompressionSignal 塞入缓冲
  → Gate.Signal() 唤醒
  → 下轮 BuildStartInjectAsync drain 到压缩产物
  → Engine 重建 Agent（`new Agent(prefix, summary, retainedHistory, host)`）

压缩期间产生的新消息 → 留在缓冲 → 新 Agent 启动时 drain，
不受旧摘要影响，全部视为"新的"。
```

L3 的唯一区别：`await` 压缩 Task，阻塞当前轮直到完成，然后强制重建 Agent。

### 8.4 压缩产物

- summary（摘要）+ retainedHistory（保留最近 N 条聊天消息，不含工具调用/思考过程）
- 保留条数可配置：`channelRetainedMessageCount`（默认 6），`channelRetainedMaxTokens`（默认 2000），条数/token 先到即截断，优先保留靠后的消息
- 压缩产物打包为 `CompressionSignal` 塞入缓冲，与正常信号一同在 `BuildStartInjectAsync` 被 drain
- 下限保护：< 5K tokens 不压，连带旧摘要一起用
- 切 mode 时立即压缩（缓存丢了就丢了，干净上下文更重要）
- 频道丢弃前触发最后一轮压缩 + 持久化

## 九、持久化

- 实时写入，每轮结束原子写（临时文件 → rename），崩溃不丢
- 频道上下文：一个频道一个 JSON 文件，路径 `Storage/{EngineType}/{ChannelId}.json`
- 引擎状态同步持久化（mode、sleep state 等），写入 `Storage/{EngineType}/_state.json`

### 频道上下文文件结构

```json
{
  "summary": "压缩后的摘要文本（null = 尚未压缩）",
  "state": {
    "mode": "working",
    "sleepState": "none",
    "updatedAt": "2026-05-19T12:00:00Z"
  },
  "rounds": [
    {
      "user": [{ "role": "user", "content": "..." }],
      "assistant": [{ "role": "assistant", "content": "..." }]
    }
  ]
}
```

- `rounds` 数组按轮次追加，每轮一组 user/assistant 消息对
- 压缩后：`summary` 更新为新摘要，`rounds` 替换为保留的对话历史
- 恢复时：`LoadContext()` → 重建 summary + rounds 到 Agent.History
- 系统循环类似结构，但路径 `Storage/SystemLoop/context.json`

## 十、总线系统

### 10.1 事件结构

```csharp
abstract class EngineEvent {
    EngineEventType Type { get; }       // Message / Timer / Signal / System / Idle
    DateTime Time { get; }
    bool Consumed { get; }
    string? TraceSignalId { get; }
    string? TraceParentSpanId { get; }
}
```

五种事件类型：`MessageEvent`、`TimerEvent`、`IdleEvent`、`SignalEvent`、`SystemEvent`。不需要 `TargetEngineId`，引擎各自判断是否处理。

### 10.2 单总线模型

只有一个 EventBus，所有事件走同一条总线。激活和消息注入分离：

| 场景 | 总线事件 | 激活方式 |
|------|---------|---------|
| 普通消息（频道） | `MessageEvent` | 唤醒 Gate → ShouldActivate 通过 → 开闸 |
| 群聊普通消息 | `MessageEvent` | 唤醒 Gate → ShouldActivate 判 false → 消息留缓冲 |
| 委托完成 | `MessageEvent` + `Gate.ForceWake()` | 消息进缓冲 + 强制开闸 |
| 调试/管理触发 | `Gate.ForceWake()` | 直接跳过 ShouldActivate |

不额外加 `bool ShouldActivate` 标记，ForceWake 就是"强制激活"的入口。

### 10.3 引擎内部总线（ModuleBus）

- 每个引擎独立实例，不和 EventBus 混淆
- Module 之间通信用，生命周期 = Engine
- 全局事件不进 ModuleBus，Module 广播不进全局

### 10.4 插件访问 EventBus

插件通过**构造函数注入**获取 EventBus 引用：

```csharp
// PluginLoader 扫描时检查构造函数，有 EventBus 参数就给
class DelegationPlugin : IInjectProvider {
    private readonly EventBus bus;
    private readonly Gate gate;  // 可选，需要强制唤醒时声明

    public DelegationPlugin(EventBus bus, Gate gate) {
        this.bus = bus;
        this.gate = gate;
    }
}
```

不需要的插件不声明即可，和 `IMemoryAccess` 注入模式一致。Gate 同理，需要强制唤醒的插件才声明。

### 10.5 引擎间通信

引擎间直接发消息走 `ISystemContext.NotifyChannel(int channelId, string content)`，将内容推入目标频道的缓冲。路径已存在，统一模型下保持不变。

### 10.6 完全替代

- TaskBridge + NotifyChannel → 总线事件
- SpawnCheck → 总线事件（MasterEngine 保持 SpawnCheck 机制，但触发源走总线）
- 强制唤醒 → `Gate.ForceWake()` 方法，不是总线上单独的事件类型

## 十一、委托板（插件驱动）

委托系统由委托插件全权负责，不走硬编码。

### 11.1 委托摘要（BuildStartInjectAsync）

每轮引擎调用时，委托插件在 `BuildStartInjectAsync` 返回极简摘要行：

```
委托：共3个，其中1个正在处理，2个正在排队
```

约 30 tokens，不影响上下文大小。

### 11.2 委托详情（事件推送）

委托提交/完成的详细信息由插件通过 EventBus 推送 `MessageEvent`，进入频道上下文的对话历史，后续轮次自然可见：

- **委托提交时**：插件收到委托提交事件 → publish `MessageEvent`("已提交委托 #N：…")
- **委托完成时**：插件收到结果回传 → publish `MessageEvent`("委托 #N 完成：…") + `Gate.ForceWake()`

### 11.3 插件结构

```csharp
class DelegationPlugin : IInjectProvider {
    private readonly EventBus bus;
    private readonly Gate gate;
    private readonly DelegationRegistry registry;

    // 构造：拿 EventBus + Gate + DelegationRegistry
    public DelegationPlugin(EventBus bus, Gate gate, DelegationRegistry registry) {
        this.bus = bus;
        this.gate = gate;
        this.registry = registry;
        // 订阅委托生命周期回调
        registry.OnDelegationSubmitted += OnSubmitted;
        registry.OnDelegationCompleted += OnCompleted;
    }

    // IInjectProvider: 只产摘要
    public Task<string> BuildStartInjectAsync() {
        return Task.FromResult(BuildSummary());
    }
    public Task<string> BuildRoundInjectAsync() => Task.FromResult<string>(null);

    // 委托事件 → 推送详情消息
    private void OnSubmitted(Delegation d) {
        bus.Publish(new MessageEvent { Message = FormatSubmission(d) });
    }
    private void OnCompleted(Delegation d) {
        bus.Publish(new MessageEvent { Message = FormatCompletion(d) });
        gate.ForceWake();  // 强制唤醒闸门
    }
}
```

## 十二、TaskEngine（新建）

- 标准 ISubEngine，短期存在
- 装 Agent（多轮推理），但上下文不带对话历史
- 并发上限由 MasterEngine 全局管理
- 队列策略：并行满了直接拒绝，不自动排队
- 工具白名单由 SystemEngine 决定，危险工具直接不给
- 任务详情 + 频道上下文在创建时注入

## 十三、MemoryService

- 可复用服务（和 Core 同级），所有引擎共享
- 双通道：
  - **自动注入**：引擎准备当前轮注入时调用 → 记忆片段塞进注入层（模型无需手动调用）
  - **手动查询**：`memory_search` 工具 → 模型主动搜索
- 工具实现底层复用 MemoryService，避免重复逻辑

## 十四、数据库分离

按功能独立为五个库：

| 库 | 内容 |
|----|------|
| Main DB | 频道消息、频道配置、用户/Person、工具配置/Profile、委托板、引擎全局状态 |
| Memory DB | MemoryEntry、Tag、Embedding、MemoryLink（正式关联）、记忆配置 |
| Dream DB | Fragment 中间产物、候选关联、Merge/Dedup/执行日志、Embedding 缓存 |
| ModelLog DB | 模型调用日志（已有） |
| Signal DB | 信号追踪 events（已有） |

- Fragment 自带 DDL，只动 Dream DB
- 每个库独立连接字符串，DI 各自管理

## 十五、DreamEngine 合并 Review

### 15.1 概述

- ReviewEngine 撤销，功能作为 DreamEngine 的 Fragment
- **DreamEngine 改为常驻引擎**（不再每次创建销毁），Gate + 总线唤醒
- 前缀常驻 → 缓存锚点一直热，即使两次睡眠间隔长

### 15.2 Fragment 按计算类型分层

| 类型 | 用什么 | 跑在 | 例子 |
|------|-------|------|------|
| FrameworkFragment | 算法/DB/Embedding，不调模型 | 走神 | Dedup、过期清理、相似度计算 |
| CoreFragment | 单次 Core 调用 | 小睡/大睡 | Weight 评估、Link 发现 |
| AgentFragment | 多轮 Agent | 大睡 | Consolidation、CodeReview、ContextReview |

走神高频、零模型成本、不依赖缓存。小睡攒够量跑单次 Core。大睡深度分析。

### 15.3 IFragment 接口

```csharp
interface IFragment {
    string Name { get; }
    FragmentType Type { get; }              // Framework / Core / Agent
    string[] RequiredTables { get; }        // DDL，动 Dream DB
    string[] NeededCores { get; }           // 依赖的 Core 名称
    int MinSleepLevel { get; }              // daydream / nap / deep
    int MaxBudgetTokens { get; }
    int ResourceCost { get; }               // 默认占用资源值（配置文件可覆盖）
    int Weight { get; }                     // 默认随机概率权重（配置文件可覆盖）
    int ParallelGroup { get; }              // 同组可并行
    string[] DataDomains { get; }           // 数据域，同域不可同时跑
    Task<bool> ShouldRun(IDreamContext ctx); // 自行判断该不该执行
    Task<FragmentResult> ExecuteAsync(FragmentScope scope);
}
```

`ShouldRun` 不检查对其他 Fragment 的依赖，只判断自己的条件。Fragment 完全自主。

`FragmentScope` 是每次执行的隔离容器：

```csharp
class FragmentScope {
    IDreamDatabase DreamDB { get; }          // Fragment 主工作区，WAL 模式，并发安全
    IMemoryDatabase MemoryDB { get; }        // 始终可用，Fragment 按需使用
    ICoreFactory CoreFactory { get; }        // 按名称获取 Core 实例
    ISignalContext SignalContext { get; }    // 独立日志隔离
    FragmentConfig OverrideConfig { get; }   // 配置覆盖值
}
```

- Fragment 只用自己需要的，两个 DB 都可用但不管连接管理
- `ICoreFactory.Get(string name)` 按 `NeededCores` 声明获取 Core

### 15.4 配置覆盖

`DreamConfig.json` 可覆盖 Fragment 的默认参数：

```json
{
  "fragments": {
    "MemoryConsolidation": {
      "enabled": true,
      "resourceCost": 20,
      "weight": 30,
      "minSleepLevel": "nap"
    },
    "CodeReview": {
      "enabled": true,
      "resourceCost": 80,
      "weight": 5,
      "minSleepLevel": "deep"
    }
  }
}
```

`resourceCost > maxResourceBudget` → 该 Fragment 永远派不出，等于禁用。

### 15.5 调度模型：资源感知优先队列 + 持续补货

```
Budget（Token 预算）= 消耗品，只减不返，耗尽不再派新任务
Resource（并行槽位）= 共享资源，任务完成返还，控制并行数
两者独立，同时约束调度
```

调度循环：

```
1. 补缓冲区
   ReadyBuffer 不够大 → 从剩余候选中按 Weight 随机抽取补满
   候选 = ShouldRun 返回 true + MinSleepLevel ≤ 当前深度

2. 尝试派发
   按 ResourceCost 降序排 ReadyBuffer
   逐个检查：cost ≤ AvailableResource → 出发，扣资源，扣预算

3. 派不出 且 没任务在跑 → 所有候选人耗尽 → 结束

4. 派不出 但 有任务在跑 → 等任意一个完成
   完成回调返还资源 → 回到步骤 1（补货+重试）

5. 大块优先填缝
   派发降序 → 大块优先
   剩余碎片资源 → 小任务自然填满
```

### 15.6 数据整合路径

```
Fragment 产出 → 写 Dream DB（中间结果）
                    │
   ┌────────────────┼────────────────┐
   │                │                │
   ↓                ↓                ↓
下个 Fragment    定期清理任务     ConsolidationFragment
读 Dream DB      清理过期中间数据   验证后写入 Memory DB
自行判断                              │
ShouldRun                          Memory DB
                                  （正式记录）
```

### 15.7 插件发现统一

PluginLoader 单次扫描，统一发现所有插件类型：

```
扫描 Plugins/*.dll
  ├─ ITool         → ToolRegistry.Register(tool)
  ├─ Component     → ComponentRegistry.Register(type)
  ├─ IWebUIProvider → ProviderRegistry.Register(provider)
  ├─ ICore         → CoreRegistry.Register(core)        ← 新增
  └─ IFragment     → FragmentRegistry.Register(fragment) ← 新增
```

同一 DLL 可同时包含多种类型（如 Fragment 自带专用 Core）。

**ICore 配置**：Core DLL 自带 `{Name}.template.json`（提示词模板 + key 占位符），安装时复制到 `Storage/Core/{Name}.json`，用户填 key。`CoreRegistry` 持有配置路径映射，创建 Core 实例时传入配置。

**ICore 构造函数**：统一 `ICore(ICoreContext ctx)`，`ICoreContext` 包含配置路径和 API 客户端。

**FragmentScope 构造**：由 DreamEngine 在每次 ExecuteAsync 前创建，注入 Dream DB + Memory DB + CoreFactory + SignalContext + 配置覆盖值。Fragment 按需使用。

**NeededCores**：Fragment 声明依赖的 Core 名称，引擎通过 `ICoreFactory` 按名提供。Fragment 自带 Core 则不声明，自行创建。

## 十六、实施顺序

1. 整体规范 doc（本文）← 当前
2. 数据库分离
3. 插件发现统一（ICore + IFragment 注册）
4. Agent 类实现
5. Gate 基类实现
6. ChannelEngine 改造（最复杂）
7. SystemEngine 改造（含委托板 + TaskEngine）
8. DreamEngine 改造（含 Fragment 体系 + FragmentScope + 调度循环 + Review 合并）
9. VisionEngine / TimerEngine 适配
