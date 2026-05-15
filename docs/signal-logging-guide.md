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
Signal.Begin(LogGroup.Adapter, $"adapter:{platform}", "消息接收", new { channelId, userId });
```

Creates a **new signal** and sets it as the current context. Only call this at top-level entry points (message arrival, scheduled task start, etc.). Everything downstream automatically belongs to this signal.

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

## Cross-Boundary Handoff

When work crosses async boundaries that break `AsyncLocal` (e.g., spawning a background task with a captured signal):

```csharp
var signalId = SignalContext.Current?.SignalId;
var parentSpan = SignalContext.Current?.CurrentSpanId; // string? (span_id hex)

_ = Task.Run(() =>
{
    Signal.Continue(signalId, parentSpan, "background", LogGroup.Engine, "后台任务");
    // ... work here is linked to the original signal
});
```

This is rare. Normal `async/await` chains propagate context automatically.

## How It Works (Brief)

```
Signal.Event(...)
  → SignalContext.Current.Event(...)     // AsyncLocal lookup
    → creates LogEvent { signal_id, scope, parent_id=CurrentSpanId, timestamp, ... }
    → LogWriter.Enqueue(evt)            // lock-free queue
      → background thread batches INSERT into SQLite (logs.db)
```

- **Zero allocation** on the hot path when below minLevel (early return)
- **Async-safe**: AsyncLocal propagates across await boundaries
- **Non-blocking**: Enqueue is a ConcurrentQueue add; DB writes happen on a dedicated thread
- **Crash-safe**: SQLite WAL mode; unflushed events in queue are lost (acceptable for logs)

**Causal linking:** `parent_id` stores the `span_id` (16-char hex GUID) of the parent span. This is generated synchronously before enqueue, so no DB round-trip is needed. The visualization builds parent-child trees by indexing open events by their `span_id` and matching children's `parent_id` against that index.

## Checklist for Adding Log Points

1. Identify the operation boundary → use `Signal.Open` for spans, `Signal.Event` for points
2. Pick the right `LogGroup`
3. Attach useful `detail` (IDs, counts, timing)
4. Use `Signal.Error`/`Signal.Warn` for failure paths
5. Don't log inside tight loops (use Debug level + guard with a condition if needed)
6. Don't duplicate what the span already captures (open/close gives you duration for free)
