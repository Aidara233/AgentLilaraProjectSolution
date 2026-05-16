# Signal Logging API — Quick Reference

This document explains how to instrument code with structured log events.
Target audience: sub-agents implementing features that need observability.

## Core Concept

Every runtime operation belongs to a **Signal** — a causal tree of events.
Signals propagate automatically via `AsyncLocal`; you never pass context manually.

```
Signal (signal_id)
 └─ open "消息处理"          ← span (has open + close)
     ├─ event "分类完成"     ← point event
     ├─ open "模型调用"      ← nested span
     │   ├─ event "首token"
     │   └─ close
     └─ close
```

## Namespace & Import

```csharp
using AgentCoreProcessor.Logging;
```

No DI needed — `Signal` is a static class. Works anywhere in `AgentCoreProcessor`.

For plugins, use the injected `ISignalLogger` interface (same methods, minus `Begin`/`Continue`).

## The 4 Patterns

### 1. Point Event (most common)

```csharp
Signal.Event(LogGroup.Engine, "配置加载完成");
Signal.Event(LogGroup.Memory, "记忆检索", new { count = 5, elapsed_ms = 42 });
```

One line. Fire and forget. Automatically inherits signal_id, scope, and parent from the current async context.

### 2. Span (timed operation)

```csharp
using var span = Signal.Open(LogGroup.Model, "模型调用", new { model = "claude", core = "Working" });
// ... do work ...
// span auto-closes when disposed (writes a "close" event)
```

Use `using var` for method-scoped spans, or `using (...)` for block-scoped:

```csharp
using (var span = Signal.Open(LogGroup.Tool, "工具执行", new { tool = toolName }))
{
    var result = await ExecuteTool();
    span.SetCloseDetail(new { success = true, result_length = result.Length });
}
```

`SetCloseDetail` attaches data to the close event (e.g., outcome, metrics).

### 3. Level Variants

```csharp
Signal.Debug(LogGroup.Engine, "详细调试信息", new { state });   // level 0
Signal.Event(LogGroup.Engine, "正常事件");                      // level 1 (default)
Signal.Warn(LogGroup.Engine, "可能有问题", new { reason });     // level 2
Signal.Error(LogGroup.Engine, "出错了", new { error = ex.Message }); // level 3
```

Debug events are filtered by `minLevel` config (default: Info). Use Debug for high-frequency diagnostics.

### 4. Signal Begin (rare — entry points only)

```csharp
var ctx = Signal.Begin(LogGroup.Adapter, $"adapter:{platform}", "消息接收", new { channelId, userId });
```

**Creates a new signal** and sets it as the current context. Only call this at top-level entry points (message arrival, scheduled task start, program startup, etc.). Everything downstream that shares the same async context automatically belongs to this signal.

### 5. Signal Continue (rare — cross-scope handoff)

```csharp
Signal.Continue(signalId, causeSpanId, $"channel:{id}", LogGroup.Engine, "频道轮次");
```

Creates a new root span in a **different scope** that is **caused by** a span in another scope. The `causeSpanId` is the span_id of the triggering event. This sets `cause_span_id` (cross-scope causation) and leaves `parent_id = null` (new root in this scope).

Use `Continue` when:
- An adapter receives a message and the channel loop picks it up (adapter scope → channel scope)
- A timer fires and triggers a channel action (timer scope → channel scope)

Do NOT use `Continue` for same-scope nesting — use `Signal.Open` for that.

## LogGroup Constants

| Constant | Use for |
|----------|---------|
| `LogGroup.Engine` | Core engine logic, lifecycle, state transitions |
| `LogGroup.Model` | LLM API calls, token usage, retries |
| `LogGroup.Tool` | Tool invocation, execution, results |
| `LogGroup.Memory` | Memory retrieval, storage, embedding |
| `LogGroup.Adapter` | Platform adapters, message I/O |
| `LogGroup.Plugin` | Plugin loading, lifecycle |

Pick the group that best describes **what subsystem** the code belongs to.

## The `detail` Parameter

Always an anonymous object. Serialized to JSON automatically.

```csharp
Signal.Event(LogGroup.Tool, "工具调用", new { tool = "speak", channel = channelId, status = "success" });
```

Rules:
- Keep it flat (no nested objects unless truly needed)
- Use snake_case keys for consistency
- Include IDs, counts, durations — things useful for debugging
- Don't include large payloads (message bodies, full responses)
- `null` is fine if there's nothing useful to attach

## When Context Is Absent

If `SignalContext.Current` is null (code running outside any signal), all `Signal.*` calls are **no-ops**. This is safe — you won't get exceptions, but events won't be recorded.

If you need to ensure logging works, verify the call site is downstream of a `Signal.Begin()` (which happens at message arrival and engine startup).

## Cross-Scope Handoff (Signal Continuation)

When work crosses scopes (e.g., adapter receives message → channel loop processes it), `AsyncLocal` does NOT propagate because the Task boundary is crossed. Code in the channel loop starts with an empty `SignalContext.Current`. Use explicit trace carriers:

```csharp
// In adapter (scope: adapter:qq-main):
var signalId = SignalContext.Current?.SignalId;
var parentSpan = SignalContext.Current?.CurrentSpanId;

// Pass via EngineEvent.TraceSignalId / TraceParentSpanId
var e = new MessageEvent(message);
e.TraceSignalId = signalId;
e.TraceParentSpanId = parentSpan;
_eventBus.Publish(e);

// In channel loop (scope: channel:2):
Signal.Continue(sigId, parentSpan, $"channel:{channelId}", LogGroup.Engine, "频道轮次");
// → creates a new root span in channel:2 with cause_span_id = parentSpan
```

**Data model:** `parent_id` stores same-scope nesting (vertical lines on trace page). `cause_span_id` stores cross-scope causation (diagonal lines). They are independent columns — one event can have both, either, or neither.

## How It Works (Brief)

```
Signal.Event(...)
  → SignalContext.Current.Event(...)     // AsyncLocal lookup
    → creates LogEvent { signal_id, scope, parent_id=CurrentSpanId, cause_span_id, timestamp, ... }
    → LogWriter.Enqueue(evt)            // lock-free queue
      → background thread batches INSERT into SQLite (logs.db)
```

- **Zero allocation** on the hot path when below minLevel (early return)
- **Async-safe**: AsyncLocal propagates across await boundaries
- **Non-blocking**: Enqueue is a ConcurrentQueue add; DB writes happen on a dedicated thread
- **Crash-safe**: SQLite WAL mode; unflushed events in queue are lost (acceptable for logs)

**Causal linking — two independent relationships:**

| Column | Stores | Meaning | Visual |
|--------|--------|---------|--------|
| `parent_id` | parent's `span_id` | Same-scope nesting ("contained within") | Vertical line |
| `cause_span_id` | trigger's `span_id` | Cross-scope causation ("triggered by") | Diagonal line |

Both are `span_id` values (16-char hex GUID), generated synchronously before enqueue. The visualization builds trees from `parent_id` and cross-scope links from `cause_span_id`. A span with no close event has its vertical line terminated at the deepest descendant row (tree-based), not at page bottom.

**Node color semantics (trace page):**

| Color | Meaning | Condition |
|-------|---------|-----------|
| Green | Completed | Span has a close event |
| Yellow | In progress | No close, but parent span also has no close (still running) |
| Red | Anomaly/crash | No close, but parent span IS closed (or startupSignal closed) |
| Gray | Interrupted | No close, but a cease event exists after the open (process was killed) |

**Node shape (signal origin):**

| Shape | Meaning | Condition |
|-------|---------|-----------|
| Double circle | Signal origin | `is_signal_origin = 1` in database |
| Single circle | Nested span or continuation | `is_signal_origin = 0` |

`is_signal_origin` is set ONLY by `Signal.Begin()` — true entry points (program start,
adapter message arrival, timer tick). `Signal.Continue()` and `Signal.Open()` do NOT
set it. The trace page reads `row.isSignalOrigin` directly from the data, no heuristics.

**Graceful shutdown:** Program.cs registers `ApplicationStopping` hook → stops adapters → stops all engines → waits 30s → closes startupSignal. Engine lifecycle spans close with reason. On kill/crash, lifecycle spans stay open → red on trace page.

**Cease event (process interruption recovery):** On startup, `LogDatabase.InsertCeaseIfNeeded()` checks for unclosed spans. If found, inserts a `type='cease'` event. The trace page uses cease events to:
- Cap unclosed span vertical lines (gray color = interrupted)
- Reset slot indentation (prevents progressive nesting across restarts)

**Trace page scope ordering:** Columns are sorted by causal priority: `system:init → system:* → timer:* → dream:* → vision:* → review:* → adapter:* → channel:*`. New scopes from real-time push trigger re-sort.

**Hover interaction:** Hovering highlights the direct causal lineage — upstream causes (via `cause_span_id`) and downstream effects, plus same-scope tree (via `parent_id`). Cross-scope lines only highlight when both endpoints are lit.

## Checklist for Adding Log Points

1. Identify the operation boundary → use `Signal.Open` for spans, `Signal.Event` for points
2. Pick the right `LogGroup`
3. Attach useful `detail` (IDs, counts, timing)
4. Use `Signal.Error`/`Signal.Warn` for failure paths
5. Don't log inside tight loops (use Debug level + guard with a condition if needed)
6. Don't duplicate what the span already captures (open/close gives you duration for free)
