# ChannelEngine Signal Instrumentation — Reference Template

The *verified* pattern for instrumenting an engine loop with Signal.
Expand to other engines by following these rules in order.

## Correctness Rules (must follow)

### 1. Engine lifecycle gets its own signalId

```csharp
// In RunAsync()
var parentCtx = SignalContext.Current;
var lifeCtx = Signal.Continue(
    SignalContext.NewSignalId(),      // ← independent signal tree
    parentCtx?.CurrentSpanId,         // cause → caller
    $"scope:{id}", LogGroup.Engine, "Name引擎", new { engineType });
// ... loop ...
lifeCtx.Close(new { reason = "shutdown" });
```

Never use `parentCtx?.SignalId` — that merges your tree into the caller's.

### 2. Session uses new signal_id, linked via cause_span_id

```csharp
sessionCtx = Signal.Continue(SignalContext.NewSignalId(), parentSpan, scope, LogGroup.Engine, "名称",
    new { ... });
// OR fallback (no upstream span):
sessionCtx = Signal.Begin(LogGroup.Engine, scope, "名称", new { ... });
```

Session creates its own signal_id. Only `cause_span_id` links back to the adapter span.
This keeps each session as an independent signal source, achieving 1:1 mapping of
signal to processing cycle. The adapter's signal_id is never reused for sessions.

### 3. Fire-and-forget: clear stale context first

```csharp
// Capture stable identifiers BEFORE the Task.Run
var causeSpanId = _sessionRootSpanId;  // session root, not inner span

_ = Task.Run(async () =>
{
    SignalContext.Restore(null);        // ← clear stale AsyncLocal
    using var ctx = Signal.Continue(
        SignalContext.NewSignalId(), causeSpanId,
        "scope:background", LogGroup.Memory, "后台任务", new { ... });
    // ... work ...
    ctx.Close(new { ... });
});
```

### 4. Capture root spanId before nesting

```csharp
_sessionRootSpanId = sessionCtx?.CurrentSpanId;  // capture BEFORE gate opens
using (var gateSpan = Signal.Open(...))
{
    // inside here, SignalContext.Current.CurrentSpanId = gate_span, NOT session root
    // always pass _sessionRootSpanId to fire-and-forget, never SignalContext.Current
}
```

This prevents inner spans (gate, round, model) from accidentally becoming
cause_span_id targets — which makes them render as double circles on trace page.

### 5. Restore parent context after close

```csharp
sessionCtx.Close(new { reason = "suspend" });
sessionCtx = null;
SignalContext.Restore(lifeCtx);  // ← restore lifecycle for subsequent Open calls
```

Without restore, subsequent `Signal.Open()` calls create spans under the disposed context.

## Content Completeness (what to log)

### Model calls — full input + output

```csharp
using (var span = Signal.Open(LogGroup.Model, "模型调用", new {
    mode, channelId, core = coreName,
    messageCount = messages.Count,
    messages = messages.Select(m => new { role = m.Role, content = m.Content }),
    imageCount = images?.Count ?? 0
}))
{
    output = await core.InvokeAsync(messages, mode);
    span.SetCloseDetail(new {
        isText = output.IsText,
        hasToolCalls = output.HasToolCalls,
        toolCount = output.ToolCalls?.Count ?? 0,
        responseText = output.IsText ? output.Text : null,
        thinking = output.Thinking,
        toolCalls = output.ToolCalls?.Select(tc => new { tool = tc.Tool, inputs = tc.Inputs })
    });
}
```

### Context assembly — full XML + memories

```csharp
using (var span = Signal.Open(LogGroup.Memory, "组装上下文", new { channelId, personId }))
{
    var (xml, images) = await builder.BuildAsync(...);
    var memories = await GetMemoriesAsync(...);
    span.SetCloseDetail(new {
        recentMsgCount, memoryCount, hasImages,
        contextXml = xml,
        memories = memories.Select(m => new { m.Content, m.Score, m.Confidence, type = ... })
    });
}
```

### Tool execution — per-tool results

```csharp
using (var span = Signal.Open(LogGroup.Tool, "执行工具", new {
    toolCount = calls.Count,
    tools = string.Join(",", calls.Select(c => c.Tool))
}))
{
    results = await executor.ExecuteAsync(calls);
    span.SetCloseDetail(new {
        successCount = results.Count(r => r.Status == "ok"),
        errorCount = results.Count(r => r.Error != null),
        results = results.Select(r => new { r.Status, r.Error })
    });
}
```

### Outgoing messages — full content

```csharp
using (var span = Signal.Open(LogGroup.Adapter, "发送消息", new {
    channelId, platform, content, replyTo, mentions
}))
{
    var sentId = await adapter.SendMessageAsync(msg);
    span.SetCloseDetail(new { messageId = sentId });
}
```

### Decision points — point events

```csharp
Signal.Event(LogGroup.Engine, "冲动值决策", new {
    channelId, decision, impulse, threshold, messageCount, idleSeconds
});
Signal.Event(LogGroup.Engine, "消息分类", new { channelId, result, content_preview });
Signal.Event(LogGroup.Engine, "模式切换", new { channelId, from, to, reason });
Signal.Event(LogGroup.Engine, "拦截器跳过", new { channelId, interceptor });
Signal.Event(LogGroup.Engine, "静音跳过", new { channelId, messageCount });
Signal.Event(LogGroup.Engine, "Working会话结束", new { channelId, totalRounds, hadSpeak });
Signal.Warn(LogGroup.Engine, "连续失败退避", new { channelId, consecutiveFailures, backoffSeconds });
```

### Error paths

```csharp
catch (Exception ex)
{
    Signal.Error(LogGroup.Engine, "处理异常", new {
        error = ex.GetType().Name, message = ex.Message, consecutiveFailures
    });
}
```

## Scope Naming Convention

| Owner | Scope | Created by |
|-------|-------|-----------|
| Program entry | `system:init` | `Signal.Begin` |
| Engine lifecycle | `{engine}:main` or `{engine}:{id}` | `Signal.Continue` (new signalId) |
| Channel lifecycle | `channel:{id}` | `Signal.Continue` (new signalId) |
| Channel session | `channel:{id}` | `Signal.Continue` (adapter's signalId) |
| Background extraction | `extraction:channel:{id}` | `Signal.Continue` (new signalId) |
| Timer tick | `timer:heartbeat` | `Signal.Begin` |
| Adapter message | `adapter:{id}` | `Signal.Begin` |

## Data-Driven Signal Origin

`Signal.Begin()` creates events with `is_signal_origin = 1` (double circle on trace).
`Signal.Continue()` and `Signal.Open()` do NOT — they have `is_signal_origin = 0`.

This means only true entry points (program start, adapter message, timer tick) get
double circles. Continuations and nested spans are single circles. The trace page
reads `row.isSignalOrigin` directly — no heuristics, no false positives.

## Verify After Each Step

1. `dotnet build` — zero errors
2. `dotnet run -- --test` — check `localhost:5000/logs/trace`
3. Verify: correct colors (no unexpected red), no false double circles,
   detail content complete when clicking nodes
4. `git commit`
