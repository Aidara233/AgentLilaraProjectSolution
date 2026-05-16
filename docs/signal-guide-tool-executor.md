# Signal Guide: ToolExecutor

> **Status:** Zero instrumentation. 87-line file, simple structure.
> **Goal:** Wrap each tool execution in a Tool-group span with full context and outcome.
> **Reference:** `signal-instrumentation-guide.md` for API conventions.

**File:** `AgentCoreProcessor/Tool/ToolExecutor.cs`

**Important:** All Signal names in the code must be in Chinese. Detail fields use English keys.
**Detail standard:** Full content in all details — no truncation. Full tool inputs, full tool outputs. Only exclude binary data and secrets.

---

## Current State

```csharp
// ExecuteAsync — calls RunSingleAsync per tool, no spans
// RunSingleAsync — resolves tool, checks disabled/auth, executes, no spans
```

The caller (ChannelEngine, SystemEngine, ReviewEngine) may already wrap the batch call in a Tool span, but individual tool execution is not instrumented. This guide adds per-tool spans inside the executor itself.

---

## Changes Needed

### 1. Add Tool-group span in RunSingleAsync

Wrap each individual tool execution. This is the core change:

```csharp
private async Task<ToolResult> RunSingleAsync(ToolCall call)
{
    var tool = toolResolver(call.Tool);
    if (tool == null)
    {
        Signal.Warn(LogGroup.Tool, $"工具未找到: {call.Tool}", new
        {
            toolName = call.Tool,
            reason = "未注册"
        });
        return new ToolResult { Status = "failed", Error = $"未知工具: {call.Tool}" };
    }

    // Open span BEFORE disabled/auth checks — these are part of tool execution
    using var toolSpan = Signal.Open(LogGroup.Tool, $"执行工具: {call.Tool}",
        new
        {
            toolName = call.Tool,
            argsCount = call.Inputs.Count,
            inputs = call.Inputs,                  // FULL inputs, no truncation
            timeout = tool.Timeout.TotalSeconds
        });

    if (ToolRegistry.IsDisabled(call.Tool))
    {
        var reason = ToolRegistry.GetDisableReason(call.Tool) ?? "未知原因";
        toolSpan.SetCloseDetail(new { status = "disabled", reason });
        return new ToolResult { Status = "failed", Error = $"工具已禁用: {reason}" };
    }

    // Permission check
    var meta = ToolRegistry.GetMeta(call.Tool);
    var permission = meta?.Permission ?? AgentLilara.PluginSDK.ToolPermission.Default;
    if (permission > AgentLilara.PluginSDK.ToolPermission.Default
        && authorizedTools != null
        && !authorizedTools.Contains(tool.Name))
    {
        toolSpan.SetCloseDetail(new
        {
            status = "unauthorized",
            requiredPermission = permission.ToString()
        });
        return new ToolResult
        {
            Status = "failed",
            Error = $"未授权使用「{tool.Name}」，管理员可用 /auth grant {tool.Name} 授权"
        };
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var cts = new CancellationTokenSource(tool.Timeout);
    try
    {
        var result = await tool.ExecuteAsync(call.Inputs, cts.Token);
        sw.Stop();

        toolSpan.SetCloseDetail(new
        {
            status = result.Status,
            success = result.Status == "success",
            elapsed_ms = sw.ElapsedMilliseconds,
            outputLength = result.Data?.Length ?? 0,
            output = result.Data,                  // FULL output
            error = result.Error
        });
        return result;
    }
    catch (OperationCanceledException)
    {
        sw.Stop();
        Signal.Warn(LogGroup.Tool, $"工具超时: {call.Tool}", new
        {
            toolName = call.Tool,
            timeout = tool.Timeout.TotalSeconds,
            elapsed_ms = sw.ElapsedMilliseconds
        });
        toolSpan.SetCloseDetail(new
        {
            status = "timeout",
            elapsed_ms = sw.ElapsedMilliseconds
        });
        return new ToolResult { Status = "failed", Error = $"执行超时（{tool.Timeout.TotalSeconds}s）" };
    }
    catch (Exception ex)
    {
        sw.Stop();
        Signal.Error(LogGroup.Tool, $"工具执行异常: {call.Tool}", new
        {
            toolName = call.Tool,
            error = ex.GetType().Name,
            message = ex.Message,
            elapsed_ms = sw.ElapsedMilliseconds
        });
        toolSpan.SetCloseDetail(new
        {
            status = "exception",
            error = ex.GetType().Name,
            elapsed_ms = sw.ElapsedMilliseconds
        });
        return new ToolResult { Status = "failed", Error = ex.Message };
    }
}
```

### 2. Args handling

Log full inputs directly in the detail object — no truncation, no helper needed:

```csharp
inputs = call.Inputs   // full list, each element full content
```

### 3. Wrap batch execution in ExecuteAsync (optional)

Optionally, add a span for the full batch execution. This is lower priority since callers (ChannelEngine) already have tool spans, but it provides a complete picture when viewing the executor in isolation:

```csharp
public async Task<List<ToolResult>> ExecuteAsync(List<ToolCall> calls)
{
    var results = new List<ToolResult>();
    foreach (var call in calls)
    {
        var result = await RunSingleAsync(call);
        results.Add(result);
        if (OnToolExecuted != null)
            await OnToolExecuted(call, result);
    }
    return results;
}
```

No additional batch span needed — callers already provide this. Individual tool spans nest under the caller's span automatically.

---

## Naming Conventions

| Event | Name | Group |
|-------|------|-------|
| Tool execution | `执行工具: {toolName}` | Tool |
| Tool not found | `工具未找到: {toolName}` (warn) | Tool |
| Tool timeout | `工具超时: {toolName}` (warn) | Tool |
| Tool exception | `工具执行异常: {toolName}` (error) | Tool |

---

## Important Notes

1. **Log full inputs and full outputs.** No truncation. Results are essential for debugging tool behavior.
2. **Span is opened before disabled/auth checks** — so the trace shows rejected executions too.
3. **Use `Signal.Warn` for timeout and not-found** — these are controlled situations, not errors.
4. **Use `Signal.Error` for unexpected exceptions** — these are real failures.
5. **Include `using System.Diagnostics`** if Stopwatch isn't already imported.

---

## Checklist

- [ ] `RunSingleAsync` opens `Signal.Open(LogGroup.Tool, ...)` before any checks
- [ ] Span open detail includes: toolName, argsCount, inputs (full), timeout
- [ ] "Tool not found" case emits `Signal.Warn`
- [ ] Disabled tool case closes span with status=disabled
- [ ] Unauthorized tool case closes span with status=unauthorized
- [ ] Successful execution close detail includes: status, success, elapsed_ms, outputLength, output (full)
- [ ] Timeout case emits `Signal.Warn` + close detail
- [ ] Exception case emits `Signal.Error` + close detail
- [ ] All names are in Chinese, descriptive
- [ ] Full inputs logged (no truncation)
- [ ] Full output logged (no truncation)
- [ ] Stopwatch used for accurate elapsed_ms (not DateTime subtraction)
