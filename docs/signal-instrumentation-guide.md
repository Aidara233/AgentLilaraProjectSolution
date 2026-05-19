# Signal Instrumentation Guide — Where and How to Place Log Points

Target audience: sub-agents tasked with adding Signal logging to modules.
Prerequisite: read `signal-logging-guide.md` for API usage.

## Core Principles

1. **Scope = execution context owner** (which engine/adapter is running)
2. **LogGroup = nature of the operation** (model call, memory retrieval, tool execution)
3. **Detail = everything useful** (IDs, counts, durations, states — no size limit)
4. **Signal.Continue sets cause_span_id** (cross-scope causation), leaves parent_id=null (new scope root)

## Signal Boundaries (Signal.Begin)

`Signal.Begin` is ONLY for when causality enters the process from outside. "Outside" means: not traceable to any existing SignalContext.

This happens in exactly two cases:
1. **Network I/O arrives** — WebSocket frame, HTTP request (adapter receives message)
2. **OS clock fires** — Timer elapsed, heartbeat tick (no prior async context)

| Trigger | Scope | Example |
|---------|-------|---------|
| External message arrives | `adapter:{adapterId}` | OneBotAdapter receives WS event |
| Timer/heartbeat fires | `timer:{engineId}` | TimerEngine tick |

**Everything else uses `Signal.Continue`** — engines, sub-agents, and channels are activated BY something that already has a signal. They continue that signal, not start a new one.

| Continuation | Scope | Gets trace from |
|-------------|-------|-----------------|
| Engine lifecycle starts | `{engine}:{id}` | SignalContext.Current (startup signal via AsyncLocal) |
| ChannelEngine wakes | `channel:{channelId}` | EngineEvent.TraceSignalId (from adapter) |
| SystemEngine processes task | `system:{engineId}` | SystemTask.TraceSignalId (from channel) |
| Sub-agent spawned | `subagent:{sessionId}` | Delegation.TraceSignalId (from channel) |
| DreamEngine starts | `dream:{engineId}` | EngineEvent.TraceSignalId (from timer) |
| VisionEngine processes | `vision:{engineId}` | (internal queue, Begin as fallback) |

## Engine Lifecycle Pattern

Every engine's `RunAsync` wraps its loop in a lifecycle signal:

```csharp
public async Task RunAsync()
{
    var parentCtx = SignalContext.Current; // startup signal (flows via AsyncLocal)
    var lifeCtx = Signal.Continue(
        SignalContext.NewSignalId(), parentCtx?.CurrentSpanId,
        $"myengine:{myId}", LogGroup.Engine, $"{EngineType}引擎",
        new { engineType = EngineType });

    try
    {
        while (!ct.IsCancellationRequested)
        {
            // Per-iteration signals (Begin or Continue from upstream)
            using var iterCtx = Signal.Begin(...);
            // ... work ...
        }
    }
    finally
    {
        lifeCtx.Close(new { reason = "shutdown" });
    }
}
```

**Key points:**
- Lifecycle uses `Continue` from startup signal → creates `cause_span_id` link (diagonal line on trace page)
- Lifecycle span lives in the engine's own scope (not the caller's scope)
- **Lifecycle name MUST include engine type**: `"Channel引擎 [群名]"`, `"System引擎"`, `"Timer引擎"` — not generic `"引擎运行"`
- Per-iteration signals are independent (they replace `SignalContext.Current` but lifecycle is closed via `lifeCtx` variable)
- On graceful shutdown: lifecycle closes with reason → green on trace page
- On crash/kill: lifecycle never closes → red on trace page (distinguishes "crashed" from "in progress")

## Context Restoration After Close

When a `SignalContext` is closed (via `Close()` or `Dispose()`), the AsyncLocal `SignalContext.Current` still points to the disposed context. Any subsequent `Signal.Open()` calls will create spans under the closed context, polluting the trace graph.

**Always restore the parent context after closing:**

```csharp
// Inside a loop that creates per-iteration contexts:
sessionCtx.Close(new { reason = "循环挂起" });
sessionCtx = null;
SignalContext.Restore(lifeCtx); // ← restore parent for subsequent Signal.Open calls
```

This ensures gate evaluations, idle checks, and other between-session work appear under the lifecycle span, not the closed session span.

**Close events automatically carry the open name.** `SignalContext.Close()` and `SpanHandle.Dispose()` now emit the open event's name in the close event. On the trace page, close nodes show `[完成] 频道会话` instead of blank `[完成] `. Detail fields carry supplementary data (counts, decisions, outcomes).

## Channel Engine Session/Round Pattern

The channel engine's loop structure is the most heavily instrumented example:

```
Channel引擎 [群名]          ← lifecycle, Continue from startup signal (independent signal_id)
  └─ 频道会话                ← per-wakeup session, new signal_id, cause_span_id → adapter
       ├─ 闸门评估            ← gate inside session (not outside)
       ├─ 处理轮次            ← one round per model interaction
       │    ├─ 组装对话上下文
       │    ├─ AI模型调用
       │    └─ 执行工具
       └─ (next round or close)
```

Key patterns:
- Session opens BEFORE gate evaluation (gate is inside session, visually connected)
- Session spans multiple rounds in Working mode (single vertical line)
- Each round adds `SetCloseDetail` with outcome (mode, toolCount, hadSpeak)
- Session close detail includes reason ("循环挂起" for pause, "cold_timeout" for exit)

## Trace Propagation Across Async Boundaries

AsyncLocal breaks when work crosses Task boundaries (producer-consumer queues, gate signals). The solution: data carriers hold trace metadata.

**Already instrumented carriers:**
- `EngineEvent.TraceSignalId / TraceParentSpanId` — auto-captured by `EventBus.Publish`
- `SystemTask.TraceSignalId / TraceParentSpanId` — auto-captured by `TaskBridge.SubmitTaskAsync`
- `Notification.TraceSignalId / TraceParentSpanId` — auto-captured by `TaskBridge.PostNotification`
- `Delegation.TraceSignalId / TraceParentSpanId` — auto-captured by `DelegationRegistry.Submit`

**Consumer pattern:**
```csharp
// When waking up to process work from a queue:
if (SignalContext.Current == null && carrier.TraceSignalId != null)
    Signal.Continue(carrier.TraceSignalId, carrier.TraceParentSpanId,
        $"myengine:{myId}", LogGroup.Engine, "operation name", new { ... });
    // → creates new root span in this scope, with cause_span_id = TraceParentSpanId
else if (SignalContext.Current == null)
    Signal.Begin(...); // Fallback: truly no upstream (shouldn't happen normally)
```

**When adding new cross-boundary carriers:** add `TraceSignalId` and `TraceParentSpanId` string? fields, capture at submit time via `SignalContext.Current`, and Continue at consume time.

## Span Placement (Signal.Open)

Spans mark operations with duration. Use `using var span = Signal.Open(...)`.

**Mandatory spans:**
- Loop body iteration (one open/close per iteration — shows how many times the loop ran)
- Loop lifetime (one open/close wrapping the entire loop — shows when it started/stopped)
- Model API calls
- Tool execution
- Memory retrieval operations
- Any async operation that might take >100ms

**Example — engine loop pattern:**
```csharp
// Loop lifetime span
using var loopSpan = Signal.Open(LogGroup.Engine, "频道循环", new { channelId });

while (!ct.IsCancellationRequested)
{
    // Per-iteration span
    using var iterSpan = Signal.Open(LogGroup.Engine, "循环迭代", new { round });
    // ... work ...
}
```

## Point Events (Signal.Event)

Point events mark instantaneous decisions or state changes. Place them at:

- **Decision outcomes**: classification result, permission check result, impulse decision
- **State transitions**: sleep state change, connection state change, mode switch
- **Completion markers**: "memory extraction done", "context compression done"
- **Handoff points**: delegation submitted, task completed, notification sent

**Do NOT place point events for:**
- Routine data flow (message passed from A to B without transformation)
- Redundant information (span open/close already captures start/end)
- Things derivable from spans (duration — the span gives you that for free)

## Level Guidelines

| Level | When | Default enabled | Example |
|-------|------|-----------------|---------|
| Info | Key decision points, state transitions, completion markers | Yes | "分类结果: 任务", "Express→Working 升级" |
| Warn | Controlled abnormal situation, bot raises alert | Yes | "连续失败 3 次，退避", "API 配额接近上限" |
| Error | Unexpected failure (even if retrying) | Yes | "模型调用失败", "WebSocket 断开" |
| Debug | Extremely detailed diagnostics for every branch | No (minLevel=Info) | "冲动值计算: base=0.3, mention=0.5, final=0.8" |

**Error means a red dot on the trace graph** — even if the system recovers via retry, the error event is visible. This is intentional: you want to see that something went wrong at that point in time.

**Debug is off by default.** Use it for information that would be overwhelming in normal operation but invaluable when diagnosing a specific issue. Think: "if I were debugging this at 3am, what would I wish I could see?"

## Scope Naming Convention

Format: `{engineType}:{instanceId}`

| Owner | Scope format | Example |
|-------|-------------|---------|
| SystemEngine | `system:{engineId}` | `system:main` |
| ChannelEngine | `channel:{channelId}` | `channel:group_12345` |
| DreamEngine | `dream:{engineId}` | `dream:main` |
| VisionEngine | `vision:{engineId}` | `vision:main` |
| TimerEngine | `timer:{engineId}` | `timer:heartbeat` |
| ReviewEngine | `review:{engineId}` | `review:nightly` |
| OneBotAdapter | `adapter:{adapterId}` | `adapter:qq-main` |
| TaskSession | `subagent:{sessionId}` | `subagent:task-abc123` |
| Program startup | `system:init` | (one-time) |

Rules:
- English, no spaces, lowercase
- Always include instance ID (even if there's only one instance today)
- Use the existing ID field from the engine/adapter (e.g., `adapterId`, `channelId`, `sessionId`)

## LogGroup Assignment

Assign by **what the operation IS**, not who calls it:

| Operation | LogGroup | Regardless of caller |
|-----------|----------|---------------------|
| LLM API request/response | Model | Even if DreamEngine calls it |
| Tool invocation | Tool | Even if SystemEngine triggers it |
| Memory read/write/embed | Memory | Even if ChannelEngine does it |
| Message send/receive | Adapter | |
| Plugin load/unload | Plugin | |
| Everything else (state, lifecycle, decisions) | Engine | |

## Detail Content

Include everything that helps diagnosis. No size limit.

```csharp
// Good — rich context
Signal.Event(LogGroup.Engine, "冲动决策", new
{
    channelId,
    decision = "respond",
    impulse = 0.85,
    reason = "direct_mention",
    messageCount = pendingMessages.Count,
    idleSeconds = idleDuration.TotalSeconds
});

// Good — error with full context
Signal.Error(LogGroup.Model, "模型调用失败", new
{
    model = cfg.Model,
    core = CoreName,
    attempt = retryCount,
    error = ex.Message,
    elapsed_ms = sw.ElapsedMilliseconds
});
```

**Include (MANDATORY — no truncation, no summarization):**
- All IDs, counts, durations, states, decisions, reasons, error messages
- Configuration values that affected the decision
- **Full model input** — every prompt message with complete content, role, images; every tool description; every system injection
- **Full model output** — complete response text, complete tool call parameters, complete token usage data
- **Full message content** — incoming and outgoing, before and after parsing
- **Full memory data** — all stored/retrieved/extracted content, scores, types, subjects, before/after states
- **Full context** — assembled context XML, memory window contents, participant snapshots

**Exclude ONLY:** Binary data (raw image bytes), secrets/tokens/keys.

Rationale: Signal logs go to a dedicated SQLite database. The trace page can render large detail fields. There is no size constraint that justifies truncating data useful for debugging. If you can reconstruct the exact state of the system from the log alone, the detail is sufficient.

## Context Propagation Rules (Verified — 2026-05-17)

These rules were validated by manually instrumenting ChannelEngine and verifying trace page correctness at each step.

### Signal isolation

- **Each engine lifecycle**: `Signal.Continue(NewSignalId(), causeSpanId, scope, ...)`
  Creates independent signal tree. `cause_span_id` links to caller (diagonal line on trace page).
  Never reuse parent's `signalId` — that merges trees and hides the engine's internal structure.
- **Session spans**: use the engine's `signalId`, not the adapter's. The session belongs to the engine
  lifecycle tree. `cause_span_id` points to the adapter span that triggered this wake-up.
- **Do NOT use `parentCtx?.SignalId`** for engine lifecycle — use `SignalContext.NewSignalId()`.

### Fire-and-forget context

- **Entry rule**: always `SignalContext.Restore(null)` first, then create fresh context via
  `Signal.Continue(NewSignalId(), causeSpanId, ...)`. This clears stale AsyncLocal from the caller.
- **Background tasks** (extraction, etc.): independent signal + independent scope.
  Example: `extraction:channel:{id}` instead of reusing `channel:{id}`.
  This gives background work its own column on the trace page, making parallelism visible.

### Context lifecycle

- After `sessionCtx.Close()`: always `SignalContext.Restore(lifeCtx)` to return to the parent.
  Without this, subsequent `Signal.Open()` calls create spans under the disposed context.
- Capture lifecycle context in variables (e.g., `lifeCtx`). Never depend on
  `SignalContext.Current` surviving across fire-and-forget `Task.Run` boundaries.
- `cause_span_id` is decorative (diagonal lines on trace page).
  `parent_id` determines nesting and color (green/yellow/red).
  They are independent columns — one event can have both, either, or neither.

### Scope naming for background tasks

| Owner | Scope format | Example |
|-------|-------------|---------|
| Channel extraction | `extraction:channel:{id}` | `extraction:channel:group_12345` |
| Channel lifecycle | `channel:{channelId}` | `channel:group_12345` |

Background tasks get their own scope so they appear as separate columns, enabling
parallelism visualization and preventing parent-close-before-child anomalies.

## Checklist for Sub-Agents

When instrumenting a module:

1. Identify the signal boundary — where does `Signal.Begin` go? (Usually: the entry point where this module starts processing something)
2. Identify the main loop — wrap it in a lifetime span, wrap each iteration in an iteration span
3. Identify model/tool/memory calls — each gets a span with `SetCloseDetail` for the outcome
4. Identify decision points — each gets a point event at Info level
5. Identify error paths — each `catch` block that represents a real failure gets `Signal.Error`
6. Identify warn-worthy situations — recoverable but notable (retries, fallbacks, timeouts)
7. Check: can you reconstruct what happened by reading only the log? If not, add more detail.
8. Check: is anything redundant with what spans already capture? If so, remove it.
