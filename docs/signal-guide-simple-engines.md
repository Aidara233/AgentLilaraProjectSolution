# Signal Guide: Simple Engines (Dream, Review, Vision, Timer)

> **Status:** Lifecycle span exists, loop body is empty of instrumentation.
> **Goal:** Add iteration spans, operation spans, decision events, and error events inside the existing lifecycle.
> **Reference:** `signal-instrumentation-guide.md` for API conventions and naming rules.

## Files Covered

| File | Lines | Has Lifecycle | Missing |
|------|-------|--------------|---------|
| `AgentCoreProcessor/Engine/Dream/DreamEngine.cs` | ~1250 | `Signal.Continue` at L75, `lifeCtx.Close` at L108 | inner spans, events, errors |
| `AgentCoreProcessor/Engine/Dream/ReviewEngine.cs` | ~489 | `Signal.Continue` at L83, `lifeCtx.Close` at L108 | inner spans, events, errors |
| `AgentCoreProcessor/Engine/Vision/VisionEngine.cs` | ~273 | `Signal.Continue` at L58, `lifeCtx.Close` at L91 | inner spans, events, errors |
| `AgentCoreProcessor/Engine/Timer/TimerEngine.cs` | ~141 | `Signal.Continue` at L44, `lifeCtx.Close` at L81 | events, errors, close detail |

**Important:** All Signal names in the code must be in Chinese. Detail fields use English keys.
**Detail standard:** Full content in all details — no truncation. Model input/output full text, full message content, full descriptions/OCR text, full decision context. Only exclude binary data and secrets.

---

## DreamEngine

**File:** `AgentCoreProcessor/Engine/Dream/DreamEngine.cs`

### 1. Add error event in lifecycle catch block

At L92-94 (RunAsync catch), add Signal.Error:

```csharp
catch (Exception ex)
{
    Signal.Error(LogGroup.Engine, "Dream引擎异常退出", new
    {
        engineType = EngineType,
        level = level.ToString(),
        error = ex.GetType().Name,
        message = ex.Message,
        stack = ex.StackTrace
    });
}
```

### 2. Add iteration spans in RunLightSleepAsync

At L199, wrap the for-loop body with a per-fragment span:

```csharp
for (int i = 0; i < maxFragments; i++)
{
    if (shouldWake) { break; }
    var fragment = await SelectFragment(isPhase2: false);
    if (fragment == null) { break; }

    using var fragSpan = Signal.Open(LogGroup.Engine, $"做梦片段: {fragment}",
        new
        {
            fragment = fragment.ToString(),
            index = i,
            total = maxFragments,
            sleepLevel = level.ToString(),
            // Full input context for model calls in this fragment
            inputMemoryIds = currentInputIds,
            inputDetails = currentDetails
        });

    try
    {
        CurrentFragment = fragment.ToString();
        CurrentFragmentStartTime = DateTime.Now;
        currentDetails = new(); currentInputIds = null; currentOutputRaw = null;
        var summary = await ExecuteFragment(fragment.Value);
        var duration = (DateTime.Now - CurrentFragmentStartTime.Value).TotalSeconds;
        fragmentRecords.Add(new FragmentRecord { /* existing code */ });
        executed++;
        FragmentsCompleted = executed;
        LastCompletedRecord = fragmentRecords[^1];

        fragSpan.SetCloseDetail(new
        {
            success = true,
            duration_sec = duration,
            summary = summary,                       // FULL summary, no truncation
            inputMemoryIds = currentInputIds,
            outputRaw = currentOutputRaw,            // FULL model output from this fragment
            details = currentDetails                 // FULL execution details
        });

        await MaybeSleepTalkAsync(summary);
    }
    catch (Exception ex)
    {
        fragmentRecords.Add(new FragmentRecord { /* existing code */ });

        Signal.Error(LogGroup.Engine, $"做梦片段异常: {fragment}", new
        {
            fragment = fragment.ToString(),
            index = i,
            sleepLevel = level.ToString(),
            error = ex.GetType().Name,
            message = ex.Message
        });
    }
}
```

### 3. Add iteration spans in RunDeepSleepAsync

Same pattern — add `Signal.Open` + `SetCloseDetail` + `Signal.Error` in both Phase 1 and Phase 2 loops.

**Phase 1 loop** (around L256):
```csharp
using var fragSpan = Signal.Open(LogGroup.Engine, $"做梦片段: {fragment}",
    new { fragment = fragment.ToString(), phase = 1, sleepLevel = "DeepSleep" });
```

**Phase 2 loop** (around L330):
```csharp
using var fragSpan = Signal.Open(LogGroup.Engine, $"做梦片段: {fragment}",
    new { fragment = fragment.ToString(), phase = 2, sleepLevel = "DeepSleep" });
```

### 4. Wrap sub-operations in spans

**CleanupExpiredMemories** (L80 in RunAsync):
```csharp
using var cleanupSpan = Signal.Open(LogGroup.Memory, "清理过期记忆",
    new { engineType = EngineType });
await CleanupExpiredMemoriesAsync();
// Add close detail in CleanupExpiredMemoriesAsync by tracking expiredCount + orphanedCount
```

**Model calls** — ConsolidationCore, WeightCore, LinkCore, CombineCore, DedupCore, SleepTalkCore all inherit from CoreBase which already has model call spans. No additional wrapping needed at DreamEngine level — the model spans from CoreBase will automatically nest under the fragment span.

### 5. Add decision events

**Sleep state transition** (L83-89):
```csharp
Signal.Event(LogGroup.Engine, "睡眠状态切换", new
{
    from = ctx.CurrentSleepState.ToString(),
    to = level switch { SleepLevel.Daydream => "Daydream", SleepLevel.Nap => "Nap", SleepLevel.DeepSleep => "DeepSleep" }
});
```

**Wake interruption** (in OnEvent, around L123-143):
```csharp
if (msg.IsMentioned)
{
    shouldWake = true;
    Signal.Event(LogGroup.Engine, "做梦被打断", new
    {
        reason = "被@提及",
        sleepLevel = level.ToString(),
        byUserId = msg.PlatformUserId
    });
}
```

**ForceWake** (L147-150):
```csharp
internal void ForceWake(string reason)
{
    shouldWake = true;
    Signal.Event(LogGroup.Engine, "强制唤醒", new { reason, sleepLevel = level.ToString() });
}
```

### 6. Add close detail to lifecycle

Enhance the existing `lifeCtx.Close` (L108):
```csharp
lifeCtx.Close(new
{
    engineType = EngineType,
    reason = shouldWake ? "被唤醒" : "正常完成",
    sleepLevel = level.ToString(),
    fragmentsExecuted = executed,
    totalFragments = maxFragments
});
```

### 7. Error events in empty catch blocks

There are several empty `catch` blocks that should emit `Signal.Error`:

- `ForceSleepTalkAsync` (L186): add `Signal.Error(LogGroup.Engine, "梦话发送失败", ...)`
- `ExecuteConsolidation` parse errors (L669, L733): add `Signal.Warn(LogGroup.Engine, "记忆整合解析失败", ...)`
- `ExecuteWeightWithSummary` parse error (L776): add `Signal.Warn(LogGroup.Engine, "权重评估解析失败", ...)`
- `ExecuteLinkWithSummary` parse error (L829): add `Signal.Warn(LogGroup.Engine, "关联分析解析失败", ...)`
- `ExecuteTrustEvaluationAsync` (L961): add `Signal.Error(LogGroup.Engine, "信任评估失败", ...)`
- `MaybeSleepTalkAsync` (L1009): add `Signal.Warn(LogGroup.Engine, "梦话发送失败", ...)`
- `PersistSessionAsync` (L1060): add `Signal.Error(LogGroup.Engine, "做梦日志持久化失败", ...)`
- `CleanupExpiredMemoriesAsync` (L1244): add `Signal.Error(LogGroup.Memory, "过期记忆清理失败", ...)`

Use `Signal.Warn` for recoverable parsing failures and `Signal.Error` for real operational failures.

---

## ReviewEngine

**File:** `AgentCoreProcessor/Engine/Dream/ReviewEngine.cs`

### 1. Add error event in lifecycle catch block

At L92-94:
```csharp
catch (Exception ex)
{
    Signal.Error(LogGroup.Engine, "Review引擎异常退出", new
    {
        engineType = EngineType,
        mode = mode.ToString(),
        error = ex.GetType().Name,
        message = ex.Message,
        stack = ex.StackTrace
    });
}
```

### 2. Add iteration spans in RunAgentLoopAsync

At L129 (inside while loop), wrap each round:
```csharp
while (!shouldStop && totalTokens < effectiveBudget)
{
    // ... wake check ...

    using var roundSpan = Signal.Open(LogGroup.Engine, "审查轮次",
        new
        {
            round,
            mode = mode.ToString(),
            tokensUsed = totalTokens,
            budget = effectiveBudget
        });

    // 1. Build messages
    var messages = BuildRoundMessages(...);

    // 2. Model call
    using var modelSpan = Signal.Open(LogGroup.Model, "AI模型调用 [审查分析]",
        new
        {
            round,
            mode = mode.ToString(),
            messageCount = messages.Count,
            useNativeTools,
            // FULL prompt messages
            messages = messages.Select(m => new
            {
                role = m.Role,
                content = m.Content,               // FULL content
                hasImage = m.ImageEmbed != null
            }).ToList(),
            estimatedTokens = messages.Sum(m => (m.Content?.Length ?? 0) / 4)
        });

    var sw = System.Diagnostics.Stopwatch.StartNew();
    // ... existing model call code ...
    sw.Stop();

    modelSpan.SetCloseDetail(new
    {
        toolCallsGenerated = toolCalls.Count,
        tokens_in = usage.InputTokens,
        tokens_out = usage.OutputTokens,
        cached_tokens = usage.CachedTokens,
        totalTokens = usage.TotalTokens,
        elapsed_ms = sw.ElapsedMilliseconds,
        finishReason = result?.StopReason,
        // FULL model output
        outputText = result?.IsText == true ? result.Text : null,
        toolCalls = toolCalls.Select(tc => new
        {
            tool = tc.Tool,
            inputs = tc.Inputs                  // FULL inputs
        }).ToList()
    });

    // 4. Tool execution
    if (toolCalls.Count > 0)
    {
        foreach (var call in toolCalls)
        {
            using var toolSpan = Signal.Open(LogGroup.Tool, $"执行审查工具: {call.Tool}",
                new
                {
                    tool = call.Tool,
                    round,
                    argsCount = call.Inputs.Count,
                    inputs = call.Inputs                 // FULL inputs
                });

            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            var result = await toolExecutor.ExecuteAsync(new List<ToolCall> { call });
            sw2.Stop();

            toolSpan.SetCloseDetail(new
            {
                success = result[0].Status == "ok",
                error = result[0].Error,
                outputLength = result[0].Data?.Length ?? 0,
                output = result[0].Data,               // FULL output
                elapsed_ms = sw2.ElapsedMilliseconds
            });
        }
    }

    roundSpan.SetCloseDetail(new
    {
        round,
        toolCallsCount = toolCalls.Count,
        tokensUsed = totalTokens,
        stopRequested = shouldStop
    });
}
```

### 3. Add span for final round

At L272-276 (RunFinalRound):
```csharp
using var finalSpan = Signal.Open(LogGroup.Engine, "收尾轮次",
    new { round, mode = mode.ToString(), reason = "预算耗尽" });
// ... existing code ...
finalSpan.SetCloseDetail(new { toolCallsCount = toolCalls.Count });
```

### 4. Add decision events

**Completion event** (when `complete` tool is called, around L203):
```csharp
case "complete":
    shouldStop = true;
    Signal.Event(LogGroup.Engine, "审查完成", new
    {
        mode = mode.ToString(),
        totalRounds = round,
        totalTokens,
        reserveUsed
    });
    break;
```

**Budget exhaustion** (before RunFinalRound is called, L272):
```csharp
Signal.Event(LogGroup.Engine, "审查预算耗尽", new
{
    mode = mode.ToString(),
    totalTokens,
    budget = effectiveBudget,
    reserveUsed
});
```

### 5. Error events in empty catch blocks

- `write_temp_memory` catch (L217): `Signal.Warn(LogGroup.Memory, "审查临时记忆写入失败", ...)`
- `save_progress` catch (L249): `Signal.Warn(LogGroup.Engine, "审查进度保存失败", ...)`
- `RunFinalRound` catch (L347): `Signal.Error(LogGroup.Engine, "审查收尾轮次失败", ...)`

### 6. Enhance lifecycle close detail

L108:
```csharp
lifeCtx.Close(new
{
    engineType = EngineType,
    mode = mode.ToString(),
    reason = shouldStop ? "主动完成" : (shouldWake ? "被打断" : "正常完成"),
    totalRounds = round,
    totalTokens,
    reserveUsed
});
```

---

## VisionEngine

**File:** `AgentCoreProcessor/Engine/Vision/VisionEngine.cs`

### 1. Add processing round span

At L81, wrap each gate iteration:
```csharp
while (IsAlive)
{
    await gate.WaitAsync(TimeSpan.FromSeconds(60));
    if (!IsAlive) break;

    using var roundSpan = Signal.Open(LogGroup.Engine, "视觉处理轮次",
        new
        {
            engineType = EngineType,
            activeTasks = _activeTasks
        });

    try
    {
        await ProcessPendingImagesAsync();
        roundSpan.SetCloseDetail(new { totalProcessed = _totalProcessed });
    }
    catch (Exception ex)
    {
        Signal.Error(LogGroup.Engine, "视觉处理异常", new
        {
            engineType = EngineType,
            error = ex.GetType().Name,
            message = ex.Message
        });
    }
}
```

### 2. Add per-image spans

In `ProcessVisionAsync` (L144), wrap the actual API call:
```csharp
using var imgSpan = Signal.Open(LogGroup.Model, "视觉描述",
    new
    {
        imageHash = record.Hash,
        localPath = record.LocalPath,
        category = record.Category,
        attempt = attempt,
        model = ctx.Vision?.GetType().Name,
        seenCount = record.SeenCount
    });

var sw = System.Diagnostics.Stopwatch.StartNew();
// ... API call ...
sw.Stop();

imgSpan.SetCloseDetail(new
{
    success = !string.IsNullOrEmpty(desc),
    description = desc,                        // FULL vision description
    descriptionLength = desc?.Length ?? 0,
    elapsed_attempts = attempt + 1,
    suspended = _visionSuspended,
    elapsed_ms = sw.ElapsedMilliseconds
});
```

In `ProcessOcrAsync` (L204):
```csharp
using var ocrSpan = Signal.Open(LogGroup.Model, "OCR识别",
    new
    {
        imageHash = record.Hash,
        localPath = record.LocalPath,
        category = record.Category
    });

var sw = System.Diagnostics.Stopwatch.StartNew();
// ... API call ...
sw.Stop();

ocrSpan.SetCloseDetail(new
{
    hasText = result.HasText,
    text = result.Text,                        // FULL OCR text
    textLength = result.Text?.Length ?? 0,
    elapsed_ms = sw.ElapsedMilliseconds
});
```

### 3. Add decision events

**Vision suspension** (L177):
```csharp
_visionSuspended = true;
_suspendReason = $"认证失败 ({ex.Message})，本轮 Vision 处理已暂停";
Signal.Warn(LogGroup.Engine, "视觉服务暂停", new
{
    reason = "认证失败",
    suspendReason = _suspendReason
});
```

**Config update** (L250-258):
```csharp
Signal.Event(LogGroup.Engine, "视觉引擎配置更新", new
{
    visionConcurrency = newConfig.VisionConcurrency,
    ocrConcurrency = newConfig.OcrConcurrency
});
```

### 4. Error events

- `ProcessVisionAsync` retry exhaustion (L195): `Signal.Error(LogGroup.Model, "视觉描述失败", ...)`
- `ProcessVisionAsync` HTTP error (L181-retry): `Signal.Warn(LogGroup.Model, "视觉API重试中", ...)` on each retry
- `ProcessOcrAsync` HTTP error (L218): `Signal.Error(LogGroup.Model, "OCR识别失败", ...)`
- `ProcessOcrAsync` general error (L222): `Signal.Error(LogGroup.Model, "OCR识别失败", ...)`

### 5. Enhance lifecycle close detail

L91:
```csharp
lifeCtx.Close(new
{
    engineType = EngineType,
    reason = "shutdown",
    totalProcessed = _totalProcessed,
    visionErrors = _visionErrors,
    ocrErrors = _ocrErrors
});
```

---

## TimerEngine

**File:** `AgentCoreProcessor/Engine/Timer/TimerEngine.cs`

### 1. Enhance heartbeat span with close detail

At L58, add close detail to the existing heartbeat span:
```csharp
using var tickSignal = Signal.Begin(LogGroup.Engine, "timer:heartbeat", "心跳",
    new
    {
        engineType = EngineType,
        intervalSeconds,
        systemEngineAlive = ctx.HasActiveEngine("System")
    });

ctx.EventBus.Publish(new TimerEvent { TimerName = "tick" });

// Phase 8: SystemEngine heartbeat check
if (ctx.HasActiveEngine("System"))
{
    lastSystemHeartbeat = System.DateTime.Now;
    alarmSent = false;
}
else
{
    var elapsed = (System.DateTime.Now - lastSystemHeartbeat).TotalHours;
    if (elapsed > 1.0 && !alarmSent)
    {
        await SendSystemCrashAlarmAsync();
        alarmSent = true;
    }
}

tickSignal.SetCloseDetail(new
{
    systemAlive = ctx.HasActiveEngine("System"),
    alarmSent
});
```

### 2. Add events

**System crash alarm** (L72):
```csharp
Signal.Error(LogGroup.Engine, "系统循环崩溃报警", new
{
    engineType = EngineType,
    elapsedHours = elapsed,
    alarmSent = true
});
```

**Interval change** (L129):
```csharp
if (e is SignalEvent signal && signal.SignalName == "timer-interval" ...)
{
    intervalSeconds = val;
    Signal.Event(LogGroup.Engine, "心跳间隔调整", new
    {
        oldInterval = intervalSeconds,
        newInterval = val
    });
}
```

### 3. Error events in catch blocks

- `SendSystemCrashAlarmAsync` catch (L121): `Signal.Error(LogGroup.Engine, "系统崩溃报警发送失败", ...)`

---

## Naming Conventions Summary

| Scope | Name pattern | Example |
|-------|-------------|---------|
| Fragment iteration | `做梦片段: {type}` | `做梦片段: Consolidation` |
| Review round | `审查轮次` | — |
| Review model call | `AI模型调用 [审查分析]` | — |
| Review tool | `执行审查工具: {name}` | `执行审查工具: complete` |
| Final round | `收尾轮次` | — |
| Vision round | `视觉处理轮次` | — |
| Vision API | `视觉描述` / `OCR识别` | — |
| Heartbeat | `心跳` (already correct) | — |

---

## Checklist

For each file, verify:

- [ ] Iteration spans wrap each loop body iteration
- [ ] Operation spans wrap model calls, API calls, and tool executions
- [ ] Decision events mark state transitions and key outcomes
- [ ] Error events in every non-trivial catch block (not just `{ }`)
- [ ] Warn events for recoverable failures (retries, parse failures)
- [ ] Close detail on every span with counts/timing/outcome
- [ ] Lifecycle close detail includes final stats (counts, errors, reason)
- [ ] All names are in Chinese, descriptive (verb + object/reason)
- [ ] All detail fields include IDs, counts, durations, states
