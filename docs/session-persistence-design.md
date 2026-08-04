# Session 持久化 + 上下文压缩 完整实施计划

> 状态：**已实施**。本文档整合持久化、压缩工具链重建、死代码清理三条线的完整方案。
>
> **⚠ 实施细节以代码为准**：本文写于 2026-07-11，落地后部分细节已演进——持久化三件套文件名/schema 以 `Engine/Session/SessionPersistence.cs` 为准（spec.json 现含 additionalComponents/excludedComponents/resolvedComponentIds，state.json 现含 activeCoreConfig/childSessionIds/effectiveParentId/connectedChannelId，formatVersion=2）；compact 已改为资源池**同池换活跃 session**（TakeOver 预定新 id），非新 GUID 从零创建；文中引用的 ChannelEngine / ChannelExpressCycle / ChannelContextBuilder 已在 2026-08-04 express 会话化重构中删除。

## 一、目标与背景

### 1.1 目标

1. **持久化**：重启后 session 完整续跑——从存档恢复 SessionId、claim、history、状态，无缝接续工作
2. **压缩工具链复活**：三层压缩（L1 提示 / L2 催促 / L3 硬保底）接入 RuntimeSession，模型可主动 compact 会话
3. **死代码清理**：移除旧 SystemEngine 删除后残存的死代码，理清持久化-压缩的旧桩

### 1.2 现状

**持久化几乎不存在**：
- `RuntimeSession.PersistStateAsync` 只写 `meta.json`（4 个字段：status / createdAt / roundCount / componentIds），丢失 history、端口、claim、WaitUntil、InitialState
- `RuntimeSession.RestoreStateAsync` 是死代码（零调用）
- `SessionEngine.RestoreAll` 是桩——扫到存档只打日志不重建
- `SessionEngine.PersistAllAsync` 仅优雅退出时调一次
- 频道占用 `_channelFullClaims` 纯内存，重启蒸发

**压缩系统完整但全是死代码**：
- `CompressionTierModule` — 已实现 L1/L2/L3 三层判定 + `SummarizationCore` 调用 + 保留窗口，但全仓无 `new` 调用
- `CompressTool` — 未被注册到 ToolRegistry
- `SummarizationCore` — 仅 `CompressionTierModule._summarizer` 引用（死路径）
- `ChannelEngine.contextSummary` — 只从持久化恢复旧值，从不更新
- `ChannelContextPersistence.SaveCompressionResult` — 零调用

**系统已有三套 Agent 循环**：
- `Agent.cs` — 被 ReviewEngine 使用
- `RuntimeSession.RunAsync` 内联循环 — Session 用
- `ChannelExpressCycle.ExecuteExpressCycleAsync` 内联循环 — Express 用

### 1.3 三套架构的判断

| 循环 | Location | Features |
|---|---|---|
| `Agent.cs` 多轮推理 | 被 ReviewEngine 使用 | 有 OnRoundCompleted 钩子 + ConversationOffset |
| `RuntimeSession.RunAsync` | Session 用 | 支持 Port-based activation / Burst / Wait |
| `ChannelExpressCycle` | Express 用 | 内联循环， escalate 后委托 Session 接管 |

**不迁移 RuntimeSession 到 Agent.cs**：Port/Burst/Wait 模型不是 Agent.cs 当前理解的形态，迁移意味着扩展 Agent.cs 来逆消化上下文。改为在 RuntimeSession 内部"借鉴"Agent.cs 的设计模式（history 字段化 + OnRoundCompleted 钩子）。

---

## 二、核心设计决策

### 2.1 compact 工具不是扳机，而是接管（决定性认知）

旧设计里 `compress` 工具的执行流是"触发压缩器对 `_history` 做摘要+截短，原地替换"。这个路径留下多个绕不开的难题：

| 原难题 | 旧方案挣扎 |
|---|---|
| tool_use / tool_result 配对半截 | 复杂的"完整对话轮次"边界识别 |
| 压缩模式特殊分支 | 主循环要绕过"无工具→注入提醒"分支 |
| L3 锁定后模型跑路无终止 | 模型可能一直输出文本不调 compress |
| 压缩后回调重新注入频道上下文 | 特殊逻辑：调一次 BuildChannelContextAsync |
| 副模型 prompt cache 利用率 | 独立调用，cache 不一定命中 |

**新方案**：compact 工具的参数就是摘要本身，执行流是"旧 session 闭合 + 新 session 启动"：

```
轮 N: 模型调 compact(summary="...") 
    → 工具 ExecuteAsync:
        1. 取 summary 参数
        2. SessionEngine.CreateSession(新 spec):
           - 新 SessionId（新 GUID）
           - ParentSessionId = 旧 sessionId
           - 组件 / InputPorts / InitActions 沿用旧 spec
           - InitialState = "[上一会话摘要]\n{summary}\n\n请继续工作"
           - OverrideLimits = 旧 limits
           - roundCount 起始 = 旧 session._roundCount（累计）
        3. 新 session 同步 claim 频道（同步生效）
        4. 返回 ToolResult + EndSession=true
    → 旧 session RunAsync 退出循环
    → 新 session RunAsync 开始（BuildStartAsync 重新注入频道信息+参与者+历史+InitialState）
```

**一切难题消失**：
- 配对完整性：compact 工具的 tool_use + tool_result 在旧 session 内是完整配对的，不会留下孤儿
- 频道上下文重新注入：新 session 走标准 CreateSession → BuildStartAsync 自然注入
- claim 切换无缝：新 session claim 同步生效，旧 session 退出释放不相干（释放查询匹配持有者 ID，新 session 接管后旧释放 POSIX 安全）
- 无压缩模式特殊分支：compact 是普通工具调用，Execute 后 EndSession=true 走正常退出
- L3 硬保底：锁定工具表只剩 compact，模型除了调 compact 必然输出参数（摘要本身就是模型写的），无 escape 路径

### 2.2 Session 压缩摘要不回写频道级

Session 持有的压缩摘要（存在 `state.json`）**只供 session 自身消费**，不回写 `ChannelContextPersistence`。

理由：
- Session 不一定绑定频道（Task/Eavesdrop 场景无频道）
- 回写会增加 claim 切换、摘要归属判断等额外复杂度
- ChannelEngine 的 `contextSummary` 死字段一并清理（见 Phase 1）

### 2.3 三层触发阈值

阈值基于"基本上所有模型都有 200k 上下文"设定（包括 Claude 3.5 Sonnet 200k、DeepSeek 128k/256k 等）。给主对话模型留充足工作空间，压缩只在真正必要时触发：

- L1 = 80000 tokens → 注入提示（约占 200k 上下文 40%，仍有大量余量，仅提醒）
- L2 = 130000 tokens → 注入催促（约占 65%，模型需考虑尽快压缩）
- L3 = 150000 tokens → 锁定工具表只剩 compact（约占 75%，留出 50k 给 compact summary 输出和新会话起步）

`CompressMinTokens = 5000`（低于此值评估为 None tier，避免极短对话误触发）。

运行时 token 估算：`history.Sum(EstimateChars) / 3`。

**AgentConfig.cs 字段同步更新**：
```csharp
public int CompressL1Tokens { get; set; } = 80000;
public int CompressL2Tokens { get; set; } = 130000;
public int CompressL3Tokens { get; set; } = 150000;
```

### 2.4 RuntimeSession 不迁移到 Agent.cs

在 RuntimeSession 内部增强：
- `messages` 从 RunAsync 局部变量提升为实例字段 `_history`
- 添加 `CompressionTierModule` 实例（仅用 Evaluate + GetInjectText）
- 每轮持久化钩子（追加 history.jsonl + 重写 state.json）
- compact 工具直接注入 `_tools`（不走 ToolRegistry）

---

## 三、Phase 1：死代码清理

| # | 位置 | 操作 | 理由 |
|---|---|---|---|
| 1 | `ChannelContextPersistence.cs:52-69` | 删方法 `SaveCompressionResult` | 零调用 |
| 2 | `ChannelEngine.cs:84` | 删字段 `contextSummary` | 只读旧值，从不更新 |
| 3 | `ChannelExpressCycle.cs:234-236` | `contextSummary` 入参改 null | 字段已删 |
| 4 | `ChannelContextBuilder.cs:85-87` | 删 `[上下文摘要]` 注入块 | summary 源已删 |
| 5 | `ChannelContextBuilder.cs:155` | 删 `summaryLen` 日志字段 | 同上 |
| 6 | `RuntimeSession.cs:823-847` | 删方法 `RestoreStateAsync` | 零调用死代码，新路径取代 |

**保留改造（不删除，后续替换）**：
- `CompressionTierModule` — Evaluate + GetInjectText 复用，CompressAsync/CompressL3Async 不再使用
- `CompressTool` — 改名 / 重新设计为 CompactTool（Phase 5）
- `SummarizationCore` — 暂保留文件，最终废止（Phase 5 决定）
- `RuntimeSession.PersistStateAsync` — 替换为完整三文件版本（Phase 3）
- `SessionEngine.RestoreAll` — 替换为真恢复（Phase 4）

---

## 四、Phase 2：SessionPersistence 工具类

**位置**：`Engine/Session/SessionPersistence.cs`（新文件）

**职责**：封装三文件读写 + 原子操作 + jsonl 追加。

### 4.1 目录结构

```
Storage/Sessions/{SessionId}/
  spec.json          ← 创建时一次写（CreateSessionSpec 全量）
  state.json         ← 每轮原子重写（运行状态快照）
  history.jsonl      ← 每轮追加（对话历史，append-only）
```

### 4.2 spec.json 格式（不可变，创建时写一次）

```json
{
  "formatVersion": 1,
  "sessionId": "a3f2c1b8e9f2",
  "createdAt": "2026-07-11T12:34:56Z",
  "componentIds": ["basic-tools", "working-tools", "file-tools", ...],
  "autoClaimChannel": true,
  "inputPorts": {
    "default": { "channelId": 123, "subStream": "normal", "weight": 5, "preloadHistory": true }
  },
  "outputPorts": {},
  "initialState": "用户请求...",
  "initActions": [ { "tool": "connect_channel", "inputs": ["123"] } ],
  "parentSessionId": null,
  "overrideLimits": { "maxLifetime": "02:00:00", "idleTimeout": "00:30:00" },
  "coreConfigName": "SessionCore"
}
```

### 4.3 state.json 格式（每轮原子重写，tmp+move）

```json
{
  "formatVersion": 1,
  "sessionId": "a3f2c1b8e9f2",
  "status": "Running",
  "createdAt": "2026-07-11T12:34:56Z",
  "roundCount": 5,
  "agentRound": 3,
  "waitUntil": "2026-07-11T13:00:00Z",
  "lastActivity": "2026-07-11T12:55:00Z",
  "compressionTier": "None",
  "connectedChannel": { "channelId": 123, "mode": "Full" },
  "channelCursors": { "123": 4500 }
}
```

### 4.4 history.jsonl 格式（追加）

每行一条 JSON Message，按写入顺序：

```jsonl
{"role":"user","content":"[频道信息]...\n"}
{"role":"assistant","content":"...","contentParts":[...]}
{"role":"user","contentParts":[{"type":"tool_result","toolUseId":"...","text":"..."}]}
```

**原子性原则**：assistant turn + 配套 tool_result turn 一起追加（`File.AppendAllLines` 批量写），避免半截轮次。

### 4.5 SessionPersistence API

```csharp
internal class SessionPersistence
{
    public SessionPersistence(string sessionId);

    // spec.json: 创建时一次写入
    public Task SaveSpecAsync(CreateSessionSpec spec, DateTime createdAt);

    // state.json: 每轮原子重写（tmp+move）
    public Task SaveStateAsync(SessionStateSnapshot state);

    // history.jsonl: 每轮追加（assistant + tool_result 一次写全）
    public Task AppendHistoryAsync(List<Message> newMessages);

    // 恢复时全量读（返回 spec + state + history）
    public (CreateSessionSpec Spec, SessionStateSnapshot State, List<Message> History) Load();

    // 清理（session 完成后可选归档压缩）
    public Task ArchiveAsync();
}

internal class SessionStateSnapshot
{
    public string SessionId { get; set; }
    public SessionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public int RoundCount { get; set; }
    public DateTime? WaitUntil { get; set; }
    public DateTime LastActivity { get; set; }
    public string? ConnectedChannelId { get; set; }
    public string? ConnectedMode { get; set; }
    public Dictionary<int, int> ChannelCursors { get; set; } = new();
}
```

### 4.6 崩溃半截处理

每轮原子追加（assistant + tool_result 一起写）后崩溃 → jsonl 持有到最后完成轮。

如果崩在一轮中段（模型已输出 assistant 但 tool_result 未落地写入）：
- jsonl 里这条 assistant 是孤儿（无 tool_result 配对）
- 恢复时 Load 检测：若最后一条是 assistant 且含 tool_use ContentParts → **丢弃这条**
- 丢弃后 _history 末尾必然是合法终点（user/tool_result 或纯文本 assistant）

### 4.7 Session 存档留档策略（重要）

**Session 结束（end_session / compact / 超时 / 异常）后，存档目录 `Storage/Sessions/{sessionId}/` 保留不删**，作为会话执行记录归档，方便查档与审计。

- `state.json` 中的 `status` 字段是判定"是否需要恢复"的唯一依据
- `RestoreAll` 仅加载 `status ∈ {Running, Crashed}` 的存档（见 §6.2）
- `status ∈ {Completed, Creating}` 的存档保留在场，但不进入活跃 session 表
- `SessionPersistence.ArchiveAsync()` 仅作"压缩存档目录到归档位置"的可选操作，不在 Phase 1-6 范围内实现（除非未来存档膨胀需要）

**WebUI /p/sessions 历史页面**仍可读取所有 status 的存档用作"会话历史浏览"，区分活跃 vs 已结束。

存档膨胀风险：单个 session history.jsonl 在 150k tokens 压缩前可达 ~5MB，长期运行累积可能 GB 级。可在未来加一个"超过 N 天的 Completed session 存档自动归档到压缩文件或删除"的清理任务，但当前阶段保留全量。

---

## 五、Phase 3：RuntimeSession 改造

### 5.1 history 提升为字段

原 `RunAsync` 局部变量 `messages`（Line 365）改为实例字段：

```csharp
private List<Message> _history = new();
public List<Message> History => _history;
```

RunAsync 内所有对 `messages` 的引用改为 `_history`。

### 5.2 SessionId 支持外部注入

恢复时复用旧 SessionId，新增 internal 构造重载：

```csharp
internal RuntimeSession(CreateSessionSpec spec, string fixedSessionId)
{
    ParentSessionId = spec.ParentSessionId;
    if (spec.OverrideLimits != null) Limits = spec.OverrideLimits;
    _componentIds.AddRange(spec.ComponentIds);
    _initialState = spec.InitialState;
    _initActions = spec.InitActions ?? new List<ToolCall>();
    SessionId = fixedSessionId;  // 复用，不重新生成
}
```

### 5.3 添加 CompressionTierModule 集成

仅用 Evaluate + GetInjectText 触发提示，**不用 CompressAsync/CompressL3Async**（被 compact 工具接管取代）：

```csharp
private CompressionTierModule? _compression;
private CompactTool? _compactTool;
private bool _l3Lockdown;  // L3 触发后下轮锁定工具表
private Dictionary<string, ITool>? _preLockdownTools;  // 备份

private void InitCompression()
{
    _compression = new CompressionTierModule(
        config: new AgentConfig(),
        getHistory: () => _history,
        onL3Triggered: () => _l3Lockdown = true
    );
    _compactTool = new CompactTool(this);  // 见 Phase 5
    _tools["compact"] = _compactTool;
    _activeTools.Add("compact");
}
```

### 5.4 压缩集成进循环

在 `RuntimeSession.RunAsync` 每轮推理前：

```csharp
// ① 估算 token
var estimatedTokens = _history.Sum(EstimateChars) / 3;
var compressHint = _compression?.GetInjectText(estimatedTokens);
if (compressHint != null)
{
    _history.Add(new Message { Role = "user", Content = compressHint });
    _compression.MarkL1Injected();
}

// ② L3 锁定：临时清空工具表只剩 compact
if (_l3Lockdown)
{
    _l3Lockdown = false;
    _preLockdownTools = new Dictionary<string, ITool>(_tools);
    _tools.Clear();
    _tools["compact"] = _compactTool!;
    _activeTools.Clear();
    _activeTools.Add("compact");
    _agentCore.AdditionalTools = new List<ITool> { _compactTool };
}
```

压缩完成后（在 CompactTool.Execute 内触发的 EndSession 路径，不走 CompressAsync → onComplete 回调）—**不需要在这里还原工具表**，因为旧 session 在 Execute 后立即退出循环。

### 5.5 L1 / L2 / L3 提示文字

阈值更新后（L1=80k / L2=130k / L3=150k），各层语义：
- L1（80k，约占 200k 上下文 40%）：温和提醒，不强制
- L2（130k，约占 65%）：明确催促，但仍允许模型在合适时机执行
- L3（150k，约占 75%）：强制锁定，工具表只剩 compact

修改 `CompressionTierModule.GetInjectText`：

```csharp
CompressionTier.L1 when !_l1Injected =>
    "[系统提示] 上下文稍大。如果当前内容不重要，可以考虑调用 compact 工具来精简会话。\n" +
    "调用时需提供一段摘要作为参数，描述：做了什么、得出了什么、下一步要做什么。",
CompressionTier.L2 =>
    "[系统警告] 会话长度已经较高，建议尽快在合适的地方调用 compact 工具。\n" +
    "compact 参数应是你对当前会话的完整总结。调用后会启动新会话承接你的摘要。",
CompressionTier.L3 =>
    "[系统强制] 上下文已达硬上限，本轮请调用 compact 工具结束当前会话并切换到新会话。\n" +
    "参数是你对会话的完整总结。请认真总结，新会话将基于你的总结继续工作。",
```

### 5.6 每轮持久化钩子

在 RunAsync 中，每轮 `tool_result` 追加到 `_history` 后：

```csharp
await PersistRoundAsync(newMessagesThisRound);
// = AppendHistoryAsync(newMessages) + SaveStateAsync(BuildStateSnapshot())
```

`newMessagesThisRound` 指本轮新增到 `_history` 的消息（assistant turn + tool_result turn + 注入的 user reminder 等）。

实现方法：记录本轮开始时 `_history.Count` 为 `oldCount`，本轮结束时 `newMessages = _history.GetRange(oldCount, _history.Count - oldCount)`。

### 5.7 替换 PersistStateAsync

```csharp
public async Task PersistStateAsync()
{
    if (_persister == null) return;
    await _persister.SaveStateAsync(BuildStateSnapshot());
}

private SessionStateSnapshot BuildStateSnapshot() => new()
{
    SessionId = SessionId,
    Status = Status,
    CreatedAt = _createdAt,
    RoundCount = _roundCount,
    WaitUntil = WaitUntil,
    LastActivity = _lastActivity,
    ConnectedChannelId = _connected.CurrentChannelId > 0 ? _connected.CurrentChannelId.ToString() : null,
    ConnectedMode = _connected.Mode.ToString(),
    ChannelCursors = ...  // 从 _host.StateFor(channelId).LastConsumedMessageId 派生
};
```

### 5.8 删除 RestoreStateAsync

已被 `SessionEngine.RestoreAll` 直接灌入路径取代（Phase 4.3）。

### 5.9 连接动作的持久化

`ConnectChannelAsync` / `DisconnectChannel` 内调用 `PersistStateAsync` 兜底（连接状态变化即时落地），避免崩溃丢失 claim 状态。

---

## 六、Phase 4：SessionEngine 改造

### 6.1 CreateSession 写 spec.json

在 `CreateSession` 末尾、return 前加：

```csharp
var persister = new SessionPersistence(session.SessionId);
await persister.SaveSpecAsync(spec, DateTime.UtcNow);
session.SetPersister(persister);
```

`RuntimeSession.SetPersister` 为新增 internal 方法。

### 6.2 RestoreAll 真恢复

**执行时序**：`SessionEngine.RunAsync` 第一个 `await Task.Delay(30s)` 之前同步完成（已有位置 Line 346），抢在适配器投递消息之前恢复 claim。

**冷启动过滤规则（重点）**：只加载 `status ∈ {Running, Crashed}` 的存档。`Completed` / `Creating` / `Restored` 状态的存档保留在场（按 §4.6 留档策略），但**不进入活跃 session 表**，不重建、不 claim 。

```csharp
private void RestoreAll()
{
    var baseDir = Path.Combine(PathConfig.StoragePath, "Sessions");
    if (!Directory.Exists(baseDir)) return;

    foreach (var dir in Directory.GetDirectories(baseDir))
    {
        var persister = new SessionPersistence(Path.GetFileName(dir));
        var (spec, state, history) = persister.Load();

        // ★ 关键过滤：只恢复活跃 session，已死的（Completed/Creating）保留存档但不加载
        if (state.Status is not (SessionStatus.Running or SessionStatus.Crashed))
            continue;  // Completed/Creating 状态的存档保留在磁盘但跳过

        // ① 先恢复频道占用（挡住 ChannelEngine）
        if (spec.AutoClaimChannel && spec.InputPorts.Count > 0)
        {
            var cid = spec.InputPorts.First().Value.ChannelId;
            ClaimChannel(state.SessionId, cid, ClaimLevel.Full);
        }

        // ② 重建 RuntimeSession（复用旧 ID）
        var session = new RuntimeSession(spec, state.SessionId);
        session.SetCurrency(_currency!);
        session.SetSessionEngine(this);

        // ③ 走标准 CreateSession 内部初始化路径（组件 / AgentCore / MessageEngine / AgentHost / Bootstrap）
        //    但跳过 BootstrapAsync（InitActions 已在旧 session 执行过，不重复 claim/订阅）
        //    实际提取一个 internal CreateSessionInternal(spec, session, skipBootstrap) 重用逻辑

        // ④ 灌入存档状态
        session.RestoreFromSnapshot(state, history);

        // ⑤ 注入 persister
        session.SetPersister(persister);

        _sessions[session.SessionId] = session;

        // ⑥ 启动 RunAsync（Task.Run）
        _ = Task.Run(async () => { ... 与 CreateSession 末尾的 Task.Run 一致 ... });
    }
}
```

**Completed status 的存档清理策略**：恢复扫描中遇到 `Completed` 状态的存档不加载，但若 `_sessions` 已经存在同 sessionId（compact 场景下旧 session 退出后的清理动作），保证不与新建 session 冲突。

**Creating status 的特例**：若程序在 `CreateSession` 创建 session 后、但还没来得及写完 state.json（status 还是 Creating）就崩溃，下次启动扫到 Creating 状态的存档也跳过——这是合理的，半成品不强求恢复。

### 6.3 RuntimeSession.RestoreFromSnapshot 方法

```csharp
internal void RestoreFromSnapshot(SessionStateSnapshot state, List<Message> history)
{
    // 崩溃半截检测：丢弃末尾孤儿 tool_use
    _history = SanitizeHistory(history);
    _roundCount = state.RoundCount;
    WaitUntil = state.WaitUntil;
    _lastActivity = state.LastActivity;
    Status = SessionStatus.Restored;

    // 频道连接恢复（claim 已由 SessionEngine 完成，这里只订阅消息流 + 设置 _connected）
    if (int.TryParse(state.ConnectedChannelId, out var cid) && cid > 0)
    {
        var mode = state.ConnectedMode == "Full" ? ConnectionMode.Full : ConnectionMode.Listen;
        // 走标准 ConnectChannelAsync，但跳过再次 Claim（已经 Claim 过）
        // ...或在 ConnectChannelAsync 里检测 Claim 已存在则跳过 ...
    }

    // per-channel 游标灌入 host.ChannelContextState
    foreach (var (chId, cursor) in state.ChannelCursors)
    {
        if (_host != null)
            _host.StateFor(chId).LastConsumedMessageId = cursor;
    }
}

private static List<Message> SanitizeHistory(List<Message> history)
{
    // 崩溃半截：末尾若是含 tool_use ContentParts 的 assistant 且无配对 tool_result → 丢弃
    var result = new List<Message>(history);
    while (result.Count > 0)
    {
        var last = result[^1];
        if (last.Role == "assistant" && last.ContentParts != null
            && last.ContentParts.Any(p => p.Type == "tool_use")
            // 下一条不是含 tool_result 的 user
            && (result.Count < 2 || result[^2].Role != "user"
                || result[^2].ContentParts?.Any(p => p.Type == "tool_result") != true))
        {
            result.RemoveAt(result.Count - 1);
            Signal.Warn(LogGroup.Engine, "恢复时丢弃孤儿 tool_use",
                new { discardedRole = last.Role });
        }
        else break;
    }
    return result;
}
```

### 6.4 PersistAllAsync 保留作兜底

引擎优雅退出时仍调用，但改为**只写 state.json**（history 已每轮追加）：

```csharp
private async Task PersistAllAsync()
{
    foreach (var (_, session) in _sessions)
    {
        try { await session.PersistStateAsync(); }
        catch { /* 持久化失败不阻塞 */ }
    }
}
```

### 6.5 SessionEngine 暴露给 CompactTool 的能力

CompactTool 需要调用 SessionEngine.CreateSession 创建新 session，需要在 SessionEngine 添加 internal 方法：

```csharp
internal IRuntimeSession CreateSessionFromCompact(CreateSessionSpec spec)
{
    // 直接走标准 CreateSession（有些参数会被 RuntimeSession 内部基于 ParentSessionId 做 roundCount 传递）
    return CreateSession(spec);
}
```

---

## 七、Phase 5：compact 工具与压缩工具链

### 7.1 新建 CompactTool

**位置**：`Tool/Core/CompactTool.cs`（新文件，旧 CompressTool 替换删除）

```csharp
internal class CompactTool : ITool
{
    private readonly RuntimeSession _owner;

    public string Name => "compact";
    public string Description =>
        "压缩会话上下文。提供一段完整的摘要（做了什么、得出了什么、下一步要做什么），" +
        "系统将启动新会话承接你的摘要。旧会话的对话历史将归档不再可访问。";
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("summary", "会话摘要：做了什么、得出了什么、下一步要做什么", 0, true)
    };

    public CompactTool(RuntimeSession owner) => _owner = owner;

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var summary = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
        if (string.IsNullOrWhiteSpace(summary))
            return new ToolResult { Status = "failed", Error = "摘要不能为空" };

        // 通过 SessionEngine 创建新 session
        var sessionEngine = _owner.GetSessionEngine();
        if (sessionEngine == null)
            return new ToolResult { Status = "failed", Error = "SessionEngine 不可用" };

        var newSessionId = sessionEngine.CreateFromCompact(_owner, summary);

        return new ToolResult
        {
            Status = "success",
            Data = $"已启动新会话 {newSessionId}，当前会话将结束。"
        };
    }

    public object GetInputSchema() => new
    {
        type = "object",
        properties = new { summary = new { type = "string", description = "会话摘要" } },
        required = new[] { "summary" }
    };
}
```

### 7.2 SessionEngine.CreateFromCompact

```csharp
internal string CreateFromCompact(RuntimeSession oldSession, string summary)
{
    // 1. 构建新 spec
    var spec = new CreateSessionSpec
    {
        ComponentIds = oldSession.ComponentIds.ToArray(),
        AutoClaimChannel = false,  // claim 由 InitActions connect_channel 接管
        InputPorts = oldSession.GetOriginalInputPorts(),  // 沿用旧 spec 端口
        InitActions = oldSession.GetOriginalInitActions(),
        InitialState = $"[上一会话摘要]\n{summary}\n\n请继续工作。",
        ParentSessionId = oldSession.SessionId,
        OverrideLimits = oldSession.Limits,
        CoreConfigName = "SessionCore"  // 沿用旧
    };

    // 2. 创建新 session（同步 claim 接管）
    var newSession = (RuntimeSession)CreateSession(spec);

    // 3. roundCount 累计（新 session 接续旧）
    newSession.InheritRoundCount(oldSession._roundCount);

    return newSession.SessionId;
}
```

### 7.3 RuntimeSession 暴露需要的 internal 接口

```csharp
internal SessionEngine? GetSessionEngine() => _sessionEngine;
internal Dictionary<string, PortSpec> GetOriginalInputPorts() => ...; // 从存储的 spec 取
internal List<ToolCall> GetOriginalInitActions() => ...;
internal void InheritRoundCount(int count) => _roundCount = count;
```

需要 RuntimeSession 在构造时保存 OriginalSpec（为 null 时新 Session 直接拿 spec）。

### 7.4 L3 锁定的执行流详图

```
轮 N:    tokens > 150k (L3)
        → GetInjectText 返回 L3 提示文字
        → _l3Lockdown = true
        → 清空 _tools，只留 compact
        → 模型本轮可选工具只有 compact
轮 N 或者 N+1 (取决于模型速度):
        → 模型输出 assistant + tool_use(compact, inputs=[summary_text])
        → ToolExecutor 调 CompactTool.ExecuteAsync
            → SessionEngine.CreateFromCompact
            → 新 session 创建并 claim 频道
            → 返回 ToolResult(success)
        → assistant + tool_result 追加到 _history  ← 善始善终
        → 每轮持久化钩子追加到 history.jsonl       ← session 完整留档
        → ApplyToolEffect 检测 compact → 返回 ToolEffect(EndBurst=false, EndSession=true)
        → break 退出循环
        → finally: ReleaseSessionClaims(old) // 已被新 session 接管，释放查询匹配不上旧持有者
        → 状态变 Completed
```

**关于 compact 的 tool_result 注入**：旧方案里 EndSession=true 路径会让循环立即退出，但 tool_result 此前已经被 RunAsync 主路径 `NativeToolMessageFormatter.FormatToolResults(...)` 追加到 _history（这是 Line 579-580 标准路径）。所以即使旧 session 退出，_history 的最后两条仍是合法的 tool_use + tool_result 配对，符合 Claude API 要求。这就是"善始善终"的来源——RunAsync 自然流走完该轮才退出。

**持久化时序**：每轮持久化钩子在 `messages.Add(NativeToolMessageFormatter.FormatToolResults(...))` 之后立即调用，所以 compact 这轮的 assistant + tool_result 会一起写入 history.jsonl。即使旧 session 在 此轮后退出，存档也是完整的可追溯记录。

### 7.5 L1 / L2 的弹性使用

- L1 上下文稍大 → 模型可选择调 compact 也可选择不调
- L2 较高 → 模型建议尽快在合适时机调（但仍有弹性）
- L3 硬上限 → 锁定工具表只剩 compact，模型除了 compact 别无选择

compact 调用前模型可在同一轮的思考中 speak 给用户一些信息（"稍等，马上压缩会话"），但这是模型的自然行为，不是强制。

### 7.6 compact 工具的运行时激活

只注入 RuntimeSession 的 `_tools`（不走 ToolRegistry），保证：
- Express 模式不暴露 compact（ChannelEngine 工具表不包含）
- Task/Eavesdrop session 也支持压缩（任何 RuntimeSession 都可用）
- 工具不污染 ToolRegistry 描述生成路径（compact 不在 ToolRegistry.All 中）

### 7.7 compact 触发的 EndSession 路径

RuntimeSession.RunAsync 的 `ApplyToolEffectAsync` 新增分支：

```csharp
case "compact":
    // 新 session 已在 CompactTool.Execute 内创建并 claim
    // 旧 session 只需退出
    return new ToolEffect(false, true);  // EndBurst=false, EndSession=true
```

### 7.8 死代码最终清理

| 位置 | 操作 | 理由 |
|---|---|---|
| `CompressTool.cs` | 整文件删除 | 被 CompactTool 取代 |
| `CompressionTierModule.cs:CompressAsync` | 删方法 | 不再需要原地压缩 |
| `CompressionTierModule.cs:CompressL3Async` | 删方法 | 同上 |
| `CompressionTierModule.cs:_summarizer` 字段 | 删 | CompressAsync 删除后无人引用 |
| `CompressionTierModule._currentSummary` / `SetSummary` | 删 | 压缩不再维持中间 summary 状态 |
| `SummarizationCore.cs` | 整文件删除（可选） | 压缩器不再被使用，可作为死代码移除或留作预留 |

如果 `SummarizationCore` 整个删，同步清理：
- `templates/Core/SummarizationCore.json` → 可留作模板归档
- `templates/Core/Prompts/SummarizationPrompt.txt` → 同上
- `Storage/Core/SummarizationCore.json` → 运行时不加载，可删
- `WebUI/Providers/ConfigProvider.cs:25` 中 `["SummarizationCore"]` 映射 → 删

### 7.9 CompressionTierModule 简化后的样子

```csharp
internal class CompressionTierModule
{
    private readonly AgentConfig _config;  // 用 CompressL1/L2/L3Tokens + CompressMinTokens
    private bool _l1Injected;
    private CompressionTier _currentTier = CompressionTier.None;
    // 阈值：L1=80k / L2=130k / L3=150k（200k 上下文模型友好）
    // CompressMinTokens=5000 阈值设低值，仅防极短对话误触发

    public CompressionTier CurrentTier => _currentTier;
    public CompressionTierModule(AgentConfig config) { _config = config; }

    public CompressionTier Evaluate(int estimatedTokens) { /* 同下 */ }
    public string? GetInjectText(int estimatedTokens) { /* L1/L2/L3 文字 */ }
    public void MarkL1Injected() => _l1Injected = true;
}

internal enum CompressionTier { None, L1, L2, L3 }
```

`onL3Triggered` 回调由 RuntimeSession 在 Evaluate 后检测 L3 切换时直接设置 `_l3Lockdown`，不需要 CompressionTierModule 持有回调。

`AgentConfig` 中 `CompressRetainedMessageCount` / `CompressRetainedMaxTokens` 字段在新方案下**不再被使用**（compact 丢弃整个 _history，模型自己复述，无保留窗口），保留字段但标记 `[Obsolete]` 或在简化时直接删除。

---

## 八、Phase 6：测试 + 文档

### 8.1 编译运行验证

```bash
cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null; dotnet build && dotnet run
```

### 8.2 --test 模式验证清单

| # | 场景 | 验证项 |
|---|---|---|
| 1 | Session 启动 | spec.json / state.json / history.jsonl 三文件实际生成 |
| 2 | Session 每轮 | history.jsonl 每轮追加两行（assistant + tool_result），state.json roundCount 递增 |
| 3 | Session 调 wait | state.json waitUntil 字段更新 |
| 4 | Session 调 connect_channel | state.json connectedChannel 变化，claim 落地 |
| 5 | Session 正常 end_session | 三文件 status=Completed |
| 6 | kill -9 进程 | 留下 status=Running 的存档 |
| 7 | 重启后 | RestoreAll 真恢复，claim 已重建，ChannelEngine 未抢频道 |
| 8 | 重启后 session 继续对话 | roundCount 接续，history 灌入，前文认知保留 |
| 9 | 手动注入超过 80k tokens 上下文 | L1 提示注入 |
| 10 | 注入到 130k | L2 催促文字注入 |
| 11 | 注入到 150k | L3 硬上限，下一轮工具表只剩 compact |
| 12 | 模型调 compact | 新 session 创建，summary 作为 InitialState，roundCount 接续 |
| 13 | compact 后 ChannelEngine | 不接管频道（新 session 已 claim） |
| 14 | compact 后旧 session | status=Completed，claim 已释放（持有者不是它） |

### 8.3 文档更新

- 本文件 `docs/session-persistence-design.md` 去"待实施"标记
- `docs/architecture-map.md` 更新：
  - Session 持久化一节：完整三文件
  - 压缩模块描述：CompressionTierModule 已接入 RuntimeSession，压缩改为 compact 接管
- `docs/core/context-management.md` 更新：
  - 三层压缩实际执行路径（compact 工具接管）
  - 旧 SummarizationCore 标记为已删除/已归档
- `CLAUDE.md` 更新：
  - Session 持久化状态从 "注释留后续" 改为"已实现"
  - 压缩工具链从死代码标记为已激活
  - 移除"SystemEngine 仍是架构一部分"类过期引用

### 8.4 顺手清理的文档残留

| 文件 | 问题 |
|---|---|
| `docs/core/agent-core.md` | 引用已删除的 `SystemEngine.cs:110` |
| `docs/signal/signal-instrumentation-guide.md` | 引用 SystemEngine scope |
| `docs/plugins/*.md` | 引用 SystemEngine 组件宿主 |
| `docs/audit/*.md` | 审计对象已删除 |

---

## 九、执行顺序

```
Phase 1（死代码清理）
   ↓
Phase 2（SessionPersistence 骨架）
   ↓
Phase 3（RuntimeSession 改造：history 字段化 / 压缩集成 / 每轮持久化）
   ↓
Phase 4（SessionEngine：CreateSession 写 spec / RestoreAll 真恢复）
   ↓
Phase 5（CompactTool 新建 / CompressTool 删除 / CompressionTierModule 简化）
   ↓
Phase 6（编译 + --test 验证 + 文档更新）
```

**依赖判断**：
- Phase 3 依赖 Phase 2（SessionPersistence API）
- Phase 4 依赖 Phase 3（RuntimeSession.RestoreFromSnapshot / SetPersister 接口）
- Phase 5 依赖 Phase 3 + Phase 4（CompactTool 调 SessionEngine.CreateFromCompact）
- Phase 1 独立，但放在前面避免后续改动时干扰旧死片段

---

## 十、关键风险与开放点

### 10.1 恢复时序的开放风险

**问题**：`SessionEngine.RunAsync` 内 `RestoreAll()` 在首个 `await Task.Delay(30s)` 之前同步调用。需确认 AutoStart 引擎顺序 + 适配器连接时机，保证"恢复+claim"先于消息路由。

**验证方法**：--test 模式下故意让 AdapterEngine 先于 SessionEngine 启动，观察是否有消息在 RestoreAll 之前投递，命中 ChannelEngine 孵化并接管频道。

**风险缓解**：若有窗口，把 claim 恢复提到 `SessionEngine` 构造函数或 `SystemEvent.Started` 同步段（在 MessageEngine 订阅者注册之前完成 claim）。

### 10.2 InitActions 在恢复时如何处理

**问题**：新 spec 的 `InitActions`（如 `connect_channel`）在旧 session 执行过，恢复时 BootstrapAsync 不应再次执行（claim 已由 SessionEngine 在 RestoreAll 中直接 ClaimChannel，不需重复）。

**方案**：RestoreAll 中调用 CreateSession 的恢复路径**跳过 BootstrapAsync**，频道订阅由 `RestoreFromSnapshot` 内调 ConnectChannelAsync 完成但传 skipClaim=true。

### 10.3 ParentSessionId 的处理

compact 创建的新 session ParentSessionId = 旧 sessionId。但旧 session 退出后从 `_sessions` 移除（或在 OnCompleted 中移除），ParentSessionId 指向的 session 不再存在。这是预期行为——ParentSessionId 用于 lineage 追溯（日志/Signal 信号），不用于运行时存活检查。

### 10.4 SessionEngine._sessions 中旧 session 什么时候清理

RunAsync 的 OnCompleted 事件触发后，应从 `_sessions` 移除。但 compact 创建新 session 后旧 session 还在 RunAsync 的 ToolExecutor 阶段，未退出循环，不能马上移除。需：
- CompactTool Execute 后设 `_skipCompletedEvent = true` 防止 OnCompleted 清理
- 旧 session 完全退出循环后再移除（正常 finally 后的清理路径）

或者更简单：OnCompleted 移除改成延迟移除（timeout 后清理）。建议在 CompactTool 内部直接 `_sessions.TryRemove(oldSessionId, out _)`，确保新 session 独立存在。

---

## 十一、api 表与依赖关系总览

### 11.1 RuntimeSession 新增 / 删除的方法

| 类型 | 方法 | 用途 |
|---|---|---|
| 新 | `RuntimeSession(spec, fixedSessionId)` 构造 | 恢复时复用旧 SessionId |
| 新 | `SetPersister(SessionPersistence)` | 注入持久化器 |
| 新 | `RestoreFromSnapshot(state, history)` | 灌入存档恢复 |
| 新 | `InitCompression()` | 初始化 CompressionTierModule + CompactTool |
| 新 | `BuildStateSnapshot()` | 生成 state.json 的对象 |
| 新 | `PersistRoundAsync(newMessages)` | 每轮持久化（appendHistory + saveState） |
| 新 | `GetSessionEngine()` | CompactTool 访问 SessionEngine |
| 新 | `GetOriginalInputPorts()` | CompactTool 重建端口 |
| 新 | `GetOriginalInitActions()` | CompactTool 重建 bootstrap |
| 新 | `InheritRoundCount(int)` | compact 后新 session 接续轮次 |
| 删 | `RestoreStateAsync()` | 死代码 |
| 改 | `PersistStateAsync()` | 改为只写 state.json |
| 改 | `RunAsync` 局部 messages | 改为 _history 字段 |

### 11.2 SessionEngine 新增 / 删除的方法

| 类型 | 方法 | 用途 |
|---|---|---|
| 新 | `CreateFromCompact(oldSession, summary)` | compact 创建新 session |
| 改 | `CreateSession(spec)` 末尾 | 写 spec.json + 注入 persister |
| 改 | `RestoreAll()` | 真恢复（含 claim 重建） |

### 11.3 新文件 / 删除文件

| 操作 | 文件 |
|---|---|
| 新 | `Engine/Session/SessionPersistence.cs` |
| 新 | `Tool/Core/CompactTool.cs` |
| 删 | `Tool/Core/CompressTool.cs`（被 CompactTool 取代） |
| 可选删 | `Core/SummarizationCore.cs`（不再使用） |

---

## 十二、文档更新检查清单

- [x] 本文档实现完成后去掉"待实施"状态
- [x] `docs/architecture-map.md` 更新 Session 持久化与压缩描述
- [x] `docs/core/context-management.md` 更新三层压缩实际路径
- [x] `CLAUDE.md` 移除"压缩/持久化留后续"等注释，更新 Session 持久化状态
- [x] `docs/core/agent-core.md` 清理 SystemEngine 过期引用
- [x] `docs/signal/signal-instrumentation-guide.md` 清理 SystemEngine scope 引用
- [x] `docs/plugins/*.md` 清理 SystemEngine 组件宿主引用
- [x] `docs/audit/*.md` 审计对象已删除的处理