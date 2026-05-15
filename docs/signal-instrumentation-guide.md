# Signal Instrumentation Guide — Where and How to Place Log Points

Target audience: sub-agents tasked with adding Signal logging to modules.
Prerequisite: read `signal-logging-guide.md` for API usage.

## Core Principles

1. **Scope = execution context owner** (which engine/adapter is running)
2. **LogGroup = nature of the operation** (model call, memory retrieval, tool execution)
3. **Detail = everything useful** (IDs, counts, durations, states — no size limit)
4. **Cross-scope lines only appear on Signal.Continue** (actual execution handoff)

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
| ChannelEngine wakes | `channel:{channelId}` | EngineEvent.TraceSignalId (from adapter) |
| SystemEngine processes task | `system:{engineId}` | SystemTask.TraceSignalId (from channel) |
| Sub-agent spawned | `subagent:{sessionId}` | Delegation.TraceSignalId (from channel) |
| DreamEngine starts | `dream:{engineId}` | EngineEvent.TraceSignalId (from timer) |
| VisionEngine processes | `vision:{engineId}` | (internal queue, Begin as fallback) |

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

**Include:** IDs, counts, durations, states, decisions, reasons, error messages, configuration values that affected the decision.

**Exclude:** Full message bodies (privacy), full model responses (too large for detail field — log a summary or length instead), binary data, secrets/tokens.

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
