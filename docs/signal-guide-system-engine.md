# Signal Guide: SystemEngine

> **Status:** ~65% complete. Has lifecycle, iteration spans, some operation spans, some events.
> **Goal:** Add model/tool spans, error events, and rich close details.
> **Reference:** `signal-instrumentation-guide.md` for API conventions.

**File:** `AgentCoreProcessor/Engine/System/SystemEngine.cs`

**Important:** All Signal names in the code must be in Chinese. Detail fields use English keys.
**Detail standard:** Full content in all details — no truncation. Full model prompt messages, full model output text, full tool inputs/outputs. Only exclude binary data and secrets.

---

## Current State

Already present:
- Lifecycle span: `Signal.Continue` at ~L185, `lifeCtx.Close` at ~L301
- Iteration spans: `Signal.Begin` at ~L221 "系统循环轮次"
- Some operation spans: "agent轮次", "上下文压缩"
- Some events: "任务队列检查", "委托待评估", "委托决策", "上下文压缩完成"

Missing:
- Model call spans (agentCore.InvokeWithHistoryAsync not wrapped in Model-group span)
- Tool execution spans (tool calls not wrapped in Tool-group spans)
- Error events in catch blocks
- Close details on operation spans

---

## Changes Needed

### 1. Wrap model calls in Model-group spans

Find where `agentCore.InvokeWithHistoryAsync` is called (around L310 inside "agent轮次" span).
Add explicit model span inside the agent round:

```csharp
using var agentRoundSpan = Signal.Open(LogGroup.Engine, "agent轮次",
    new
    {
        sessionId = session.Id,
        round = session.Round,
        delegationId = delegation?.Id
    });

// Add model span before the model call
using var modelSpan = Signal.Open(LogGroup.Model, "AI模型调用 [系统决策]",
    new
    {
        sessionId = session.Id,
        delegationType = delegation?.Type,
        delegationReason = delegation?.Reason,
        messageCount = contextMessages.Count,
        estimatedTokens = contextMessages.Sum(m => m.Content?.Length ?? 0) / 4,
        core = agentCore.CoreName,
        model = agentCore.Config.Model,
        caller = agentCore.CallerTag,
        // FULL prompt messages
        messages = contextMessages.Select(m => new
        {
            role = m.Role,
            content = m.Content              // FULL content
        }).ToList()
    });

var sw = System.Diagnostics.Stopwatch.StartNew();
var result = await agentCore.InvokeWithHistoryAsync(...);
sw.Stop();

modelSpan.SetCloseDetail(new
{
    decision = result?.Decision,
    toolCallsCount = toolCalls.Count,
    tokens_in = usage?.InputTokens,
    tokens_out = usage?.OutputTokens,
    cached_tokens = usage?.CachedTokens,
    totalTokens = (usage?.InputTokens ?? 0) + (usage?.OutputTokens ?? 0),
    stopReason = result?.StopReason,
    elapsed_ms = sw.ElapsedMilliseconds,
    // FULL model output
    outputText = result?.IsText == true ? result.Text : null,
    toolCalls = toolCalls.Select(tc => new
    {
        tool = tc.Tool,
        inputs = tc.Inputs                   // FULL inputs
    }).ToList()
});
```

### 2. Wrap each tool execution in Tool-group spans

Inside the tool execution loop (after the model call), wrap each tool call:

```csharp
foreach (var call in toolCalls)
{
    using var toolSpan = Signal.Open(LogGroup.Tool, $"执行工具: {call.Tool}",
        new
        {
            toolName = call.Tool,
            sessionId = session.Id,
            delegationId = delegation?.Id,
            argsCount = call.Inputs.Count,
            inputs = call.Inputs                    // FULL inputs, no truncation
        });

    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    var result = await toolExecutor.ExecuteAsync(new List<ToolCall> { call });
    sw2.Stop();

    toolSpan.SetCloseDetail(new
    {
        success = result[0].Status == "success",
        error = result[0].Error,
        outputLength = result[0].Data?.Length ?? 0,
        output = result[0].Data,                   // FULL output
        elapsed_ms = sw2.ElapsedMilliseconds
    });
}
```

### 3. Enhance context compression span

The existing "上下文压缩" span at ~L428 — add richer open detail and close detail:

```csharp
using var compressSpan = Signal.Open(LogGroup.Engine, "上下文压缩",
    new
    {
        sessionId = session.Id,
        currentTokens = session.ContextTokens,
        threshold = compressThreshold,
        messageCount = session.Messages.Count,
        roundsToKeep = roundsToKeep
    });

// ... existing compression logic ...

compressSpan.SetCloseDetail(new
{
    messagesBefore = messagesBefore,
    messagesAfter = messagesAfter,
    tokensBefore = tokensBefore,
    tokensAfter = tokensAfter,
    summary = summary,                         // FULL compression summary
    summaryLength = summary?.Length ?? 0,
    droppedMessageIds = droppedMessages?.Select(m => m.Id).ToList()
});

Signal.Event(LogGroup.Engine, "上下文压缩完成", new
{
    sessionId = session.Id,
    tokensBefore,
    tokensAfter,
    messagesRemoved = messagesBefore - messagesAfter,
    summary = summary                         // FULL summary in event too
});
```

### 4. Enhance iteration span

The "系统循环轮次" span at ~L221 — add richer detail:

```csharp
var iterCtx = Signal.Begin(LogGroup.Engine, $"system:{engineId}", "系统循环轮次",
    new
    {
        engineId,
        pendingTaskCount = pendingTasks.Count,
        pendingNotifyCount = pendingNotifications.Count,
        hasDelegation = pendingDelegations.Any(),
        activeSessionCount = activeSessions.Count,
        uptime = DateTime.UtcNow - startTime
    });
```

And close detail:
```csharp
iterCtx.SetCloseDetail(new
{
    tasksProcessed = tasksProcessed,
    notificationsProcessed = notificationsProcessed,
    delegationsEvaluated = delegationsEvaluated
});
iterCtx.Close();
```

### 5. Add decision events with rich context

**Delegation evaluation** (around L360, enhance existing event):
```csharp
Signal.Event(LogGroup.Engine, "委托决策", new
{
    delegationId = delegation.Id,
    type = delegation.Type,
    fromChannelId = delegation.FromChannelId,
    verdict = verdict,
    reason = reason,
    hasActiveSession = activeSessions.ContainsKey(delegation.FromChannelId)
});
```

**Task acceptance** (when a task is picked up):
```csharp
Signal.Event(LogGroup.Engine, "任务受理", new
{
    taskId = task.Id,
    type = task.Type,
    priority = task.Priority,
    sourceId = task.SourceId,
    queueDepth = pendingTasks.Count
});
```

### 6. Add error events in all catch blocks

Every `catch` block that currently just sets variables or is empty should emit `Signal.Error`:

```csharp
catch (Exception ex)
{
    Signal.Error(LogGroup.Engine, "系统循环处理异常", new
    {
        phase = "task-processing",  // identify which phase failed
        taskId = task?.Id,
        delegationId = delegation?.Id,
        error = ex.GetType().Name,
        message = ex.Message,
        stack = ex.StackTrace
    });
}
```

Scope each error to the specific operation that failed. Use descriptive phase names:
- `"task-processing"` for task execution failures
- `"delegation-evaluation"` for delegation decision failures
- `"notification-dispatch"` for notification delivery failures
- `"context-compression"` for compression failures
- `"agent-round"` for agent loop failures

### 7. Enhance lifecycle close detail

L301:
```csharp
lifeCtx.Close(new
{
    engineType = EngineType,
    reason = "shutdown",
    totalIterations = iterationCount,
    totalTasksProcessed = totalTasks,
    totalDelegationsEvaluated = totalDelegations,
    uptime = DateTime.UtcNow - startTime
});
```

---

## Naming Conventions

| Operation | Name | Group |
|-----------|------|-------|
| Iteration | `系统循环轮次` | Engine |
| Agent round | `agent轮次` | Engine |
| Model call | `AI模型调用 [系统决策]` | Model |
| Tool execution | `执行工具: {toolName}` | Tool |
| Context compression | `上下文压缩` | Engine |
| Delegation decision | `委托决策` | Engine |
| Task acceptance | `任务受理` | Engine |

---

## Checklist

- [ ] Model call wrapped in `Signal.Open(LogGroup.Model, ...)` with FULL prompt messages (all content, roles)
- [ ] Model span close detail includes FULL output text, FULL tool calls, usage, decision, timing
- [ ] Tool execution wrapped in `Signal.Open(LogGroup.Tool, ...)` per tool call with FULL inputs
- [ ] Tool span close detail includes FULL output, success/error, timing
- [ ] Iteration span has rich open detail (counts, IDs, state) and close detail (processed counts)
- [ ] Context compression span has before/after detail + FULL summary content
- [ ] Decision events carry full context (IDs, reasons, verdicts)
- [ ] Every catch block that represents a real failure has `Signal.Error`
- [ ] Error events identify which phase failed, include FULL error messages and stacks
- [ ] All names are in Chinese, descriptive
- [ ] All detail fields include IDs, counts, durations, decisions
- [ ] No truncation anywhere — full content in all detail fields
