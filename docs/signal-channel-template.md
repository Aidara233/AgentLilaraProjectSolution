# Signal Instrumentation — Reference Template

The *verified* pattern for instrumenting engine loops with Signal.
Based on ChannelEngine's full implementation (2026-05-21).

## Correctness Rules (must follow)

### 1. Engine lifecycle gets its own signalId

```csharp
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

Session creates its own signal_id. Only `cause_span_id` links back.

### 3. Fire-and-forget: clear stale context first

```csharp
var causeSpanId = _sessionRootSpanId;  // capture BEFORE Task.Run

_ = Task.Run(async () =>
{
    SignalContext.Restore(null);
    using var ctx = Signal.Continue(
        SignalContext.NewSignalId(), causeSpanId,
        "scope:background", LogGroup.Memory, "后台任务", new { ... });
    ctx.Close(new { ... });
});
```

### 4. Capture root spanId before nesting

```csharp
_sessionRootSpanId = sessionCtx?.CurrentSpanId;
using (var gateSpan = Signal.Open(...))
{
    // inside: CurrentSpanId = gate_span, NOT session root
    // always pass _sessionRootSpanId to fire-and-forget
}
```

### 5. Restore parent context after close

```csharp
sessionCtx.Close(new { reason = "suspend" });
sessionCtx = null;
SignalContext.Restore(lifeCtx);
```

## Content Standards (what to log)

### Model calls — full I/O with ContentParts

```csharp
using (var modelSpan = Signal.Open(LogGroup.Model, $"模型调用 R{round + 1}",
    new
    {
        round = round + 1,
        messageCount = messages.Count,
        messages = messages.Select(m => m.ContentParts != null
            ? (object)new { m.Role, parts = m.ContentParts.Select(p => new { p.Type, p.Text, p.ToolName, p.ToolInput, p.ToolUseId, p.IsError }) }
            : new { m.Role, content = m.Content })
    }))
{
    output = await core.InvokeAsync(messages, mode);
    modelSpan.SetCloseDetail(new
    {
        responseText = output.Text,
        thinking = output.Thinking,
        toolCalls = output.ToolCalls?.Select(tc => new { tc.Tool, tc.Inputs, tc.ToolUseId })
    });
}
```

Key: preserve ContentParts structure (text/image/tool_use/tool_result) in input.
Output includes thinking and full toolCalls with IDs.

### Tool execution — full inputs + per-result status/data/error

```csharp
var toolNames = string.Join(", ", calls.Select(c => c.Tool));
using (var toolSpan = Signal.Open(LogGroup.Tool, $"工具: {toolNames}",
    new
    {
        toolCount = calls.Count,
        calls = calls.Select(c => new { c.Tool, c.Inputs, c.ToolUseId })
    }))
{
    results = await executor.ExecuteAsync(calls);
    toolSpan.SetCloseDetail(new
    {
        results = calls.Zip(results, (c, r) => new
        {
            tool = c.Tool,
            status = r.Status,
            data = r.Data,
            error = r.Error
        })
    });
}
```

Key: log full `Data` (not just status), since 7-day auto-deletion makes storage acceptable.

### Memory recall — independent span

```csharp
using var memSpan = Signal.Open(LogGroup.Memory, $"记忆检索 p:{personId}",
    new { personId, channelId, query });
try
{
    var items = await memorySvc.RecallAsync(query, personId, topK);
    memSpan.SetCloseDetail(new
    {
        result = "ok",
        count = items.Count,
        memories = items.Select(m => new { m.Content, m.Confidence, m.Score })
    });
}
catch (TimeoutException)
{
    memSpan.SetCloseDetail(new { result = "timeout" });
}
catch (Exception ex)
{
    memSpan.SetCloseDetail(new { result = "error", error = ex.GetType().Name, message = ex.Message });
}
```

### Compression — span with token estimates + full summary

```csharp
var totalTokens = history.Sum(m => (m.Content?.Length ?? 0)) / 3;
using var span = Signal.Open(LogGroup.Engine, $"上下文压缩 ({totalTokens}t, {tier})",
    new { historyCount = history.Count, estimatedTokens = totalTokens,
          tier = tier.ToString(), hasPriorSummary = currentSummary != null });
// ... compress ...
span.SetCloseDetail(new
{
    compressedCount = toCompress.Count,
    retainedCount = retained.Count,
    retainedTokens = tokenCount,
    summaryLength = summary?.Length ?? 0,
    summary = summary  // full text — acceptable with 7-day retention
});
```

### Context assembly — summary Event

```csharp
Signal.Event(LogGroup.Engine, "上下文组装完成", new
{
    channelId,
    mode = isWorkingMode ? "working" : "express",
    totalMessages = msgs.Count,
    prefixLen = fixedPrefix?.Length ?? 0,
    summaryLen = contextSummary?.Length ?? 0,
    newMessageCount = activeBatch?.Count ?? 0,
    estimatedTokens = msgs.Sum(m => (m.Content?.Length ?? 0)) / 3
});
```

### Outgoing messages — full content

```csharp
using var speakSpan = Signal.Open(LogGroup.Adapter, "发送消息",
    new { channelId, content, replyTo, mentions });
var sentId = await adapter.SendMessageAsync(msg);
speakSpan.SetCloseDetail(new { messageId = sentId });
```

### Decision points — point Events

```csharp
Signal.Event(LogGroup.Engine, "冲动值决策", new { channelId, decision, impulse, threshold, messageCount, idleSeconds });
Signal.Event(LogGroup.Engine, "消息分类", new { channelId, result, content_preview });
Signal.Event(LogGroup.Engine, "模式切换", new { channelId, from, to, reason });
Signal.Event(LogGroup.Engine, "Working会话结束", new { channelId, totalRounds, hadSpeak });
```

## Span Name Convention

Span names must be informative at a glance — no need to click into detail panel.

| Pattern | Example |
|---------|---------|
| 模型调用 + 轮次 | `"模型调用 R3"` |
| Express模型 + 频道 | `"Express模型调用 ch:5"` |
| 工具 + 名称列表 | `"工具: speak, memory"` |
| 记忆检索 + person | `"记忆检索 p:12"` |
| 压缩 + token + tier | `"上下文压缩 (12000t, L2)"` |
| 闸门 + 消息数 | `"闸门评估 (3条消息)"` |
| 处理轮次 + 模式 | `"处理轮次 [Working]"` |
| 系统循环 + 事件数 | `"系统循环轮次"` (detail has counts) |

## Error Handling

All catch blocks MUST have Signal logging. No empty catches.

```csharp
// Functional failure → Error
catch (Exception ex)
{
    Signal.Error(LogGroup.Engine, "处理异常", new { error = ex.GetType().Name, message = ex.Message });
}

// Degraded but tolerable → Warn
catch (Exception ex)
{
    Signal.Warn(LogGroup.Engine, $"InjectProvider失败: {p.GetType().Name}",
        new { provider = p.GetType().Name, error = ex.Message });
}
```

Decision guide:
- `Signal.Error` — feature broken (send media failed, compression failed, model call failed)
- `Signal.Warn` — degraded tolerance (plugin init, notification send, single provider)

## Scope Naming Convention

| Owner | Scope | Created by |
|-------|-------|-----------|
| Program entry | `system:init` | `Signal.Begin` |
| Engine lifecycle | `{engine}:main` or `{engine}:{id}` | `Signal.Continue` (new signalId) |
| Channel session | `channel:{id}` | `Signal.Continue` (new signalId) |
| System cycle iteration | `system:main` | `Signal.Begin` |
| Background extraction | `extraction:channel:{id}` | `Signal.Continue` (new signalId) |
| Timer tick | `timer:heartbeat` | `Signal.Begin` |
| Adapter message | `adapter:{id}` | `Signal.Begin` |

## Data-Driven Signal Origin

- `Signal.Begin()` → `is_signal_origin = 1` (double circle on trace)
- `Signal.Continue()` / `Signal.Open()` → `is_signal_origin = 0` (single circle)

Only true entry points get double circles.

## Verify After Each Step

1. `dotnet build` — zero errors
2. `dotnet run -- --test` — check `localhost:5000/logs/trace`
3. Verify: correct colors, no false double circles, detail content complete
4. `git commit`
