# Signal Guide: ChannelEngine

> **Status:** ~40% complete. Has lifecycle/session/round/gate/model/tool spans, but most are too thin.
> **Goal:** Add full model input/output, message send/receive content, memory operation data, decision context to every span.
> **Reference:** `signal-instrumentation-guide.md` for API conventions.

**File:** `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs` (~1512 lines)

**Important:** All Signal names in the code must be in Chinese. Detail fields use English keys.
**Detail standard:** Full content in all details — no truncation, no summarization. Full model prompt messages (every role, content, tool description, injection), full model output text, full tool call parameters, full message content (send/receive), full memory data (content, scores, types), full context XML, full extraction results. Only exclude binary data and secrets.

---

## Current State

Already present:
- Lifecycle span: `Signal.Continue(..., "Channel引擎 [{channelName}]", ...)` — L261-263
- Session span: `Signal.Continue(..., "频道会话", ...)` or `Signal.Begin(...)` — L303-309
- Gate evaluation span: `Signal.Open(..., "闸门评估", ...)` — L313-318
- Round span: `Signal.Open(..., "处理轮次", ...)` — L336-367
- Context assembly span: `Signal.Open(..., "组装对话上下文", ...)` — L625-654
- Model call span: `Signal.Open(..., "AI模型调用", ...)` — L346-356
- Tool execution span: `Signal.Open(..., "执行工具", ...)` — L888-897
- Trace signal capture in `EnqueueMessage` — L203-211
- Error event in catch block — L376-377

### What's Thin vs. What's Missing

| Span/Area | Current Detail | Problem |
|-----------|---------------|---------|
| **Model call** (L346) | `{ mode, channelId }` open; `{ isText, hasToolCalls, toolCount }` close | **No prompt content, no model output, no token usage, no timing, no model name** |
| **Message send** (speakModule callbacks) | None | **No Signal at all** — content, replyTo, mentions, sentId not logged |
| **Message receive** (EnqueueMessage) | Only stores trace IDs | **No event/span** — message content, sender, channel not Signal-logged |
| **Gate evaluation** (L313) | `{ hasMessages, isWorkingMode }` open; `{ passed }` close | **No impulse value, threshold, batch content summary, interceptor decisions, mute state** |
| **Context assembly** (L625) | `{ channelId, personId }` open; `{ recentMsgCount, memoryCount, hasImages }` close | **No context XML content, no memory results content, no image details** |
| **Session** (L303) | `{ channelId, mode }` open; `{ reason }` close | **No rounds count, messages processed, tools executed, total duration** |
| **Lifecycle** (L261) | `{ engineType, channelId, channelName }` open; `{ engineType, channelId, reason }` close | **No total rounds, total errors, uptime, final state** |
| **Tool execution** (L888) | `{ toolCount, tools }` open; `{ successCount, errorCount }` close | **No per-tool results, no tool inputs/outputs, no timing per tool** |
| **Memory fetch** (FetchMemoryAsync) | None | **No Signal span** |
| **Memory extraction** (RunExtractionAsync) | None | **No Signal span for the extraction loop** |
| **Memory store calls** (via signalDispatchModule) | None | **No Signal at call site** |
| **Interceptor chain** (PrepareContextAsync) | None | **No Signal per interceptor** |
| **Watch rule matching** (CheckWatchRulesAsync) | None | **No Signal events** |
| **Mode transitions** (DecideNext) | None | **No Signal events for express→working, working→express** |
| **Impulse decision** (ShouldRespond) | None | **No Signal event** |
| **Trust progress** (IncrementDailyProgressAsync) | None | **No Signal event** |
| **Image processing** (ResolveImagePresentationAsync) | None | **No Signal span** |
| **Poke markers** (ProcessPokeMarkers) | None | **No Signal span** |
| **Message segmentation** (SendSegmentsAsync) | None | **No Signal per segment** |
| **Alert handling** (HandleAlertAsync) | None | **No Signal span** |
| **Error catch blocks** | Only main try/catch has Signal.Error | **Empty catch in GetCachedMemoryAsync, GetCachedQueryIntentAsync, RunExtractionAsync** |

---

## Changes Needed

### 1. Enhance Model Call Span — Full Input + Full Output

**Location:** L346-356

This is the most critical enhancement. The current span captures almost nothing about what the model actually saw or produced.

```csharp
ModelOutput output;
using (var modelSpan = Signal.Open(LogGroup.Model, "AI模型调用",
    new
    {
        mode = mode.ToString(),
        channelId,
        channelName,
        callerTag = agentCore.CallerTag,
        model = agentCore.CurrentModel,       // add property if not exposed
        core = agentCore.CurrentCore,          // add property if not exposed
        isNative = agentCore.UseNativeTools,
        messageCount = messages.Count,
        estimatedInputTokens = messages.Sum(m => (m.Content?.Length ?? 0) / 4), // rough estimate
        // FULL prompt context — every message
        prompt = messages.Select(m => new
        {
            role = m.Role,
            // Include FULL content, no truncation
            content = m.Content,
            hasImage = m.ImageEmbed != null,
            imageCount = m.ImageEmbed?.Count ?? 0
        }).ToList(),
        systemPromptSections = new
        {
            hasToolDescriptions = !string.IsNullOrEmpty(toolDescs),
            toolDescriptions = toolDescs,  // full tool descriptions
            hasNativeContext = nativeContext != null,
            nativeContext = nativeContext,  // full native context injection
            hasComponentSections = componentHost != null,
            hasInterceptorInjections = interceptorInjections.Count > 0,
            interceptorInjections = interceptorInjections.Count > 0
                ? interceptorInjections.ToList() : null
        }
    }))
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    output = await agentCore.InvokeAsync(messages, mode);
    sw.Stop();

    modelSpan.SetCloseDetail(new
    {
        isText = output.IsText,
        hasToolCalls = output.HasToolCalls,
        toolCount = output.ToolCalls?.Count ?? 0,
        elapsed_ms = sw.ElapsedMilliseconds,
        // FULL model output text
        outputText = output.IsText ? output.Text : null,
        // FULL tool calls with all parameters
        toolCalls = output.HasToolCalls
            ? output.ToolCalls!.Select(tc => new
            {
                tool = tc.Tool,
                inputs = tc.Inputs,          // full inputs, no truncation
                inputCount = tc.Inputs.Count
            }).ToList()
            : null,
        // Token usage (if available from AgentCore)
        inputTokens = output.InputTokens,
        outputTokens = output.OutputTokens,
        totalTokens = (output.InputTokens ?? 0) + (output.OutputTokens ?? 0),
        finishReason = output.FinishReason,
        modelUsed = output.ModelUsed
    });
}
```

**Note:** If `AgentCore` doesn't expose `CurrentModel`/`CurrentCore` properties or `ModelOutput` doesn't have token fields, those need to be added as separate small changes. The ModelCallLog already tracks tokens — the Signal span should capture the same data.

### 2. Add Message Send Spans

**Location:** `WireModuleCallbacks` — `speakModule.OnSpeak` (L431-444) and `speakModule.OnSendMedia` (L445-457)

Each outgoing message needs its own Signal span with full content:

```csharp
speakModule.OnSpeak = async (rawText) =>
{
    if (currentLastMsg == null || currentLastSc == null || currentParticipantSnapshot == null) return;
    unrespondedMessageCount = 0;
    var (content, replyTo, mentions) = ParseBotOutput(rawText, currentParticipantSnapshot);

    using var sendSpan = Signal.Open(LogGroup.Adapter, "发送消息",
        new
        {
            platform = currentLastMsg.Platform,
            channelId = currentLastMsg.ChannelId,
            rawText,                            // full raw bot output before parsing
            content,                             // full parsed content
            replyTo,
            mentions,
            mentionCount = mentions?.Count ?? 0,
            isReply = replyTo != null
        });

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var sentId = await ctx.Adapters.SendMessageAsync(currentLastMsg.Platform, new OutgoingMessage
    {
        ChannelId = currentLastMsg.ChannelId,
        Content = content,
        ReplyTo = replyTo,
        Mentions = mentions
    });
    sw.Stop();

    await ctx.Session.SaveBotMessageAsync(currentLastSc.Channel.Id, content, sentId);

    sendSpan.SetCloseDetail(new
    {
        sentId,
        success = sentId != null,
        elapsed_ms = sw.ElapsedMilliseconds,
        contentLength = content?.Length ?? 0
    });
};
```

Same pattern for `OnSendMedia`:

```csharp
speakModule.OnSendMedia = async (type, text, attachments) =>
{
    if (currentLastMsg == null || currentLastSc == null) return;
    unrespondedMessageCount = 0;

    using var sendSpan = Signal.Open(LogGroup.Adapter, "发送媒体",
        new
        {
            platform = currentLastMsg.Platform,
            channelId = currentLastMsg.ChannelId,
            mediaType = type,
            text,
            attachmentCount = attachments?.Count ?? 0,
            attachmentPaths = attachments?.Select(a => a.LocalPath ?? a.Url).ToList()
        });

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var sentId = await ctx.Adapters.SendMessageAsync(currentLastMsg.Platform, new OutgoingMessage
    {
        ChannelId = currentLastMsg.ChannelId,
        Content = text ?? "",
        Attachments = attachments
    });
    sw.Stop();

    var desc = $"[发送{type}]" + (string.IsNullOrEmpty(text) ? "" : $" {text}");
    await ctx.Session.SaveBotMessageAsync(currentLastSc.Channel.Id, desc, sentId);

    sendSpan.SetCloseDetail(new
    {
        sentId,
        success = sentId != null,
        elapsed_ms = sw.ElapsedMilliseconds
    });
};
```

**Also add spans for `SendSegmentsAsync`** (L1346-1372) — the multi-segment sender. Each segment send needs its own span, or wrap the whole method:

```csharp
private async Task SendSegmentsAsync(string text, IncomingMessage lastMsg,
    SessionContext lastSc, Dictionary<int, ParticipantInfo> participantSnapshot)
{
    var segments = text
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim())
        .Where(s => s.Length > 0)
        .ToList();

    using var segSpan = Signal.Open(LogGroup.Adapter, "分条发送消息",
        new
        {
            platform = lastMsg.Platform,
            channelId = lastMsg.ChannelId,
            segmentCount = segments.Count,
            fullText = text    // full original text before splitting
        });

    string? firstReplyTo = null;
    var sentIds = new List<string?>();
    var rng = new Random();
    for (int i = 0; i < segments.Count; i++)
    {
        if (i > 0)
            await Task.Delay(rng.Next(600, 2000));
        var (content, replyTo, mentions) = ParseBotOutput(segments[i], participantSnapshot);
        if (i == 0) firstReplyTo = replyTo;
        var sentId = await ctx.Adapters.SendMessageAsync(lastMsg.Platform, new OutgoingMessage
        {
            ChannelId = lastMsg.ChannelId,
            Content = content,
            ReplyTo = i == 0 ? firstReplyTo : null,
            Mentions = mentions
        });
        sentIds.Add(sentId);
        await ctx.Session.SaveBotMessageAsync(lastSc.Channel.Id, content, sentId);
    }

    segSpan.SetCloseDetail(new
    {
        segmentCount = segments.Count,
        sentCount = sentIds.Count(id => id != null),
        sentIds,
        firstReplyTo
    });
}
```

### 3. Add Message Receive Event

**Location:** `EnqueueMessage` (L203-222)

When a new message arrives, log it as a point event with full content:

```csharp
public void EnqueueMessage(IncomingMessage msg, SessionContext sc, string? traceSignalId = null, string? traceParentSpanId = null)
{
    lock (bufferLock)
    {
        buffer.Add((msg, sc));
        lastBufferTime = DateTime.Now;
        CollectImagePaths(msg);
        _traceSignalId = traceSignalId ?? SignalContext.Current?.SignalId;
        _traceParentSpanId = traceParentSpanId ?? SignalContext.Current?.CurrentSpanId;
    }

    Signal.Event(LogGroup.Adapter, "消息入队",
        new
        {
            channelId,
            channelName,
            messageId = msg.MessageId,
            platform = msg.Platform,
            content = msg.Content,                // FULL content
            contentLength = msg.Content?.Length ?? 0,
            senderName = sc.Person?.Name ?? sc.User?.PlatformId,
            senderUserId = sc.User?.Id,
            personId = sc.Person?.Id,
            isMentioned = msg.IsMentioned,
            isPrivate = msg.IsPrivate,
            isSystemEvent = msg.IsSystemEvent,
            hasAttachments = msg.Attachments?.Count > 0,
            attachmentCount = msg.Attachments?.Count ?? 0,
            attachmentTypes = msg.Attachments?.Select(a => a.Type.ToString()).ToList(),
            bufferSize = buffer.Count,
            participantCount = recentParticipants.Count,
            traceSignalId = _traceSignalId,
            traceParentSpanId = _traceParentSpanId
        });

    recentParticipants.AddOrUpdate(
        sc.User.Id,
        ParticipantInfo.From(sc.User, sc.Person, msg),
        (_, _) => ParticipantInfo.From(sc.User, sc.Person, msg));
    impulseTracker.Accumulate(msg, recentParticipants.Count, msg.IsSystemEvent);
    ScheduleBufferSignal();

    CheckWatchRulesAsync(msg, sc);
}
```

### 4. Enhance Gate Evaluation Span — Why Did the Gate Pass or Block?

**Location:** L313-318

The gate evaluation is the most critical decision in the loop. Log everything that goes into it:

```csharp
bool prepareResult;
using (var gateSpan = Signal.Open(LogGroup.Engine, "闸门评估",
    new
    {
        hasMessages = batch?.Count ?? 0,
        isWorkingMode,
        isInWorkingSession,
        channelId,
        channelName,
        impulse = impulseTracker.Impulse,
        threshold = ctx.ImpulseConfig.Threshold,
        affinity = channelConfig.Affinity,
        muteMode = ctx.MuteMode,
        sleepState = ctx.CurrentSleepState.ToString(),
        lastCompletionAge = LastCompletionTime != null
            ? (DateTime.Now - LastCompletionTime.Value).TotalSeconds : (double?)null,
        participantCount = recentParticipants.Count,
        pendingImageCount = pendingImageInfos.Count,
        bufferWindowSeconds = ctx.ImpulseConfig.BufferWindowSeconds,
        coldTimeoutSeconds = ctx.ImpulseConfig.ColdTimeoutSeconds,
        // Batch content summary — full message contents
        batchMessages = batch?.Select(b => new
        {
            messageId = b.Message.MessageId,
            content = b.Message.Content,          // FULL content
            senderName = b.Context.Person?.Name ?? b.Context.User?.PlatformId,
            isMentioned = b.Message.IsMentioned,
            isPrivate = b.Message.IsPrivate,
            hasAttachments = b.Message.Attachments?.Count > 0
        }).ToList()
    }))
{
    prepareResult = await PrepareContextAsync(batch);

    // Collect WHY the gate made this decision
    gateSpan.SetCloseDetail(new
    {
        passed = prepareResult,
        reason = !prepareResult
            ? (ctx.MuteMode ? "muted"
                : isInWorkingSession ? "working_continue"
                : batch == null || batch.Count == 0 ? "no_messages"
                : !impulseTracker.ShouldRespond(batch, LastCompletionTime) ? "impulse_below_threshold"
                : "interceptor_blocked")
            : "proceed",
        impulse = impulseTracker.Impulse,
        threshold = ctx.ImpulseConfig.Threshold,
        isWorkingSession = isInWorkingSession,
        // If blocked by interceptor, which one
        interceptorDecision = !prepareResult && batch != null && batch.Count > 0
            ? "see_interceptor_section" : null
    });
}
```

### 5. Add Impulse Decision Event

**Location:** `PrepareContextAsync` L556, after `impulseTracker.ShouldRespond`

```csharp
if (!impulseTracker.ShouldRespond(batch, LastCompletionTime))
{
    Signal.Event(LogGroup.Engine, "冲动值决策",
        new
        {
            channelId,
            decision = "skip",
            impulse = impulseTracker.Impulse,
            threshold = ctx.ImpulseConfig.Threshold,
            messageCount = batch.Count,
            lastCompletionAge = (DateTime.Now - LastCompletionTime.Value).TotalSeconds,
            reason = "impulse_below_threshold"
        });

    TrackMemoryExtraction(batch, currentLastSc);
    return false;
}
```

Also add event when impulse IS sufficient and gate passes:

```csharp
// After the impulse check passes (but before the actual processing):
Signal.Debug(LogGroup.Engine, "冲动值决策",
    new
    {
        channelId,
        decision = "proceed",
        impulse = impulseTracker.Impulse,
        threshold = ctx.ImpulseConfig.Threshold,
        messageCount = batch?.Count ?? 0,
        isWorkingMode
    });
```

### 6. Add Interceptor Chain Spans

**Location:** `PrepareContextAsync` L518-553

```csharp
if (interceptors.Count > 0)
{
    var interceptCtx = new AgentLilara.PluginSDK.MessageInterceptContext { /* ... */ };

    using var interceptorSpan = Signal.Open(LogGroup.Engine, "拦截器链",
        new
        {
            interceptorCount = interceptors.Count,
            interceptorNames = interceptors.Select(i => new
            {
                name = i.GetType().Name,
                priority = i.Priority
            }).ToList(),
            messageCount = batch.Count,
            sleepState = ctx.CurrentSleepState.ToString(),
            hasMention = batch.Any(b => b.Message.IsMentioned)
        });

    var interceptorResults = new List<object>();
    foreach (var interceptor in interceptors)
    {
        var result = await interceptor.OnBeforeProcessAsync(interceptCtx);
        interceptorResults.Add(new
        {
            interceptor = interceptor.GetType().Name,
            action = result.Action.ToString(),
            hasInjection = result.PromptInjection != null,
            injection = result.PromptInjection
        });

        if (result.Action == AgentLilara.PluginSDK.InterceptAction.Skip)
        {
            interceptorSpan.SetCloseDetail(new
            {
                blocked = true,
                blockedBy = interceptor.GetType().Name,
                results = interceptorResults
            });
            TrackMemoryExtraction(batch, currentLastSc);
            return false;
        }
        if (result.Action == AgentLilara.PluginSDK.InterceptAction.Handled)
        {
            interceptorSpan.SetCloseDetail(new
            {
                handled = true,
                handledBy = interceptor.GetType().Name,
                results = interceptorResults
            });
            TrackMemoryExtraction(batch, currentLastSc);
            return false;
        }
        if (result.PromptInjection != null)
            interceptorInjections.Add(result.PromptInjection);
    }

    interceptorSpan.SetCloseDetail(new
    {
        blocked = false,
        injectionCount = interceptorInjections.Count,
        results = interceptorResults
    });
}
```

### 7. Enhance Context Assembly Span — Full Context Content

**Location:** L625-654

```csharp
using (var ctxSpan = Signal.Open(LogGroup.Memory, "组装对话上下文",
    new
    {
        channelId,
        channelName,
        personId = currentLastSc.Person.Id,
        personName = currentLastSc.Person.Name,
        isWorkingMode,
        batchMessageCount = batch?.Count ?? 0,
        // Full batch message contents
        batchMessages = batch?.Select(b => new
        {
            messageId = b.Message.MessageId,
            content = b.Message.Content,          // FULL content
            senderName = b.Context.Person?.Name ?? b.Context.User?.PlatformId,
            senderUserId = b.Context.User?.Id
        }).ToList()
    }))
{
    var recentMessages = await ctx.Session.GetContextByChannelAsync(channelId);
    var effectiveBatch = batch ?? new List<(IncomingMessage, SessionContext)>();
    var (xml, imageEmbeds) = await contextBuilder.BuildContextXmlAsync(
        effectiveBatch, recentMessages, currentParticipantSnapshot);

    if (imageEmbeds.Count > 0)
        currentImageEmbeds = imageEmbeds;
    else
        currentImageEmbeds = null;
    currentContextXml = xml;

    var memoryResults = await GetCachedMemoryAsync(currentLastSc, currentLastMsg.Content);
    memoryWindowModule.SetMemories(memoryResults);

    ctxSpan.SetCloseDetail(new
    {
        recentMsgCount = recentMessages.Count,
        memoryCount = memoryResults.Count,
        hasImages = imageEmbeds.Count > 0,
        imageCount = imageEmbeds.Count,
        // FULL context XML
        contextXml = xml,
        contextXmlLength = xml?.Length ?? 0,
        // FULL memory results
        memories = memoryResults.Select(m => new
        {
            id = m.Id,
            content = m.Content,                   // FULL content
            score = m.Score,
            confidence = m.Confidence,
            type = m.Type,
            subject = m.Subject,
            isTemp = m.IsTemp,
            isPersona = m.IsPersona
        }).ToList(),
        // Recent messages used for context
        recentMessages = recentMessages.Select(m => new
        {
            id = m.Id,
            content = m.Content,                   // FULL content
            senderName = m.SenderName,
            isFromBot = m.IsFromBot
        }).ToList()
    });
}
```

### 8. Add Memory Fetch Span

**Location:** `GetCachedMemoryAsync` (L1038) → `FetchMemoryAsync` (L1071)

Wrap the actual fetch in a span:

```csharp
private async Task<List<ScoredMemory>> FetchMemoryAsync(int personId, int channelId, string query)
{
    var intent = await GetCachedQueryIntentAsync();

    using var memSpan = Signal.Open(LogGroup.Memory, "获取频道记忆",
        new
        {
            personId,
            channelId,
            query,                                 // FULL query
            hasIntent = intent != null,
            keywordCount = intent?.Keywords.Count ?? 0,
            subjectCount = intent?.Subjects.Count ?? 0,
            keywords = intent?.Keywords,
            subjects = intent?.Subjects
        });

    var sw = System.Diagnostics.Stopwatch.StartNew();
    List<ScoredMemory> results;
    if (intent != null && (intent.Keywords.Count > 0 || intent.Subjects.Count > 0))
    {
        results = await ctx.MemorySvc.RecallAsync(
            personId, channelId,
            query, intent, topK: 10, includeLinks: true, includePersona: true);
    }
    else
    {
        results = await ctx.MemorySvc.RecallAsync(
            personId, channelId,
            query, topK: 10, includeLinks: true, includePersona: true);
    }
    sw.Stop();

    memSpan.SetCloseDetail(new
    {
        resultCount = results.Count,
        elapsed_ms = sw.ElapsedMilliseconds,
        topScores = results.Take(5).Select(m => m.Score).ToList(),
        // FULL memory results
        results = results.Select(m => new
        {
            id = m.Id,
            content = m.Content,                   // FULL content
            score = m.Score,
            confidence = m.Confidence,
            type = m.Type,
            subject = m.Subject,
            isTemp = m.IsTemp,
            isPersona = m.IsPersona
        }).ToList()
    });

    return results;
}
```

### 9. Add Memory Extraction Span

**Location:** `RunExtractionAsync` (L1147-1262)

The extraction loop is completely uninstrumented. Add a lifecycle span around the whole extraction + per-batch spans:

```csharp
private async Task RunExtractionAsync(SessionContext context)
{
    if (extractionRunning) return;
    extractionRunning = true;
    extractionCts = new CancellationTokenSource();
    var ct = extractionCts.Token;

    using var extractSpan = Signal.Open(LogGroup.Memory, "记忆提取任务",
        new
        {
            channelId,
            channelName,
            personId = context.Person?.Id,
            isActive = LastCompletionTime != null
                && (DateTime.Now - LastCompletionTime.Value).TotalMinutes < 5,
            lastExtractedMessageId,
            autoExtractionEnabled = channelConfig.AutoExtractionEnabled
        });

    var batchResults = new List<object>();
    try
    {
        if (lastExtractedMessageId < 0)
        {
            var channel = await ctx.Session.GetChannelAsync(channelId);
            lastExtractedMessageId = channel?.LastExtractedMessageId ?? 0;
        }

        totalMessageCount = await ctx.Session.GetMessageCountByChannelAsync(channelId);
        extractedMessageCount = await ctx.Session.GetMessageCountUpToAsync(
            channelId, lastExtractedMessageId);

        int batchIndex = 0;
        while (!ct.IsCancellationRequested)
        {
            totalMessageCount = await ctx.Session.GetMessageCountByChannelAsync(channelId);
            extractedMessageCount = await ctx.Session.GetMessageCountUpToAsync(
                channelId, lastExtractedMessageId);

            var newMessages = await ctx.Session.GetMessagesAfterIdAsync(
                channelId, lastExtractedMessageId, limit: 50);
            if (newMessages.Count < 2) break;

            latestMessageId = newMessages[^1].Id;

            bool isActive = LastCompletionTime != null
                && (DateTime.Now - LastCompletionTime.Value).TotalMinutes < 5;
            int threshold = isActive
                ? channelConfig.ActiveExtractionThreshold
                : channelConfig.LurkingExtractionThreshold;

            if (newMessages.Count < threshold) break;

            // --- Per-batch extraction span ---
            using var batchSpan = Signal.Open(LogGroup.Memory, "提取批次",
                new
                {
                    batchIndex,
                    channelId,
                    newMessageCount = newMessages.Count,
                    threshold,
                    isActive,
                    // FULL new messages
                    newMessages = newMessages.Select(m => new
                    {
                        id = m.Id,
                        content = m.Content,           // FULL content
                        senderName = m.SenderName,
                        isFromBot = m.IsFromBot
                    }).ToList(),
                    lastExtractedId = lastExtractedMessageId
                });
            // ---

            var contextMessages = lastExtractedMessageId > 0
                ? await ctx.Session.GetMessagesBeforeIdAsync(channelId, lastExtractedMessageId, limit: 20)
                : new List<UserMessage>();

            var recentMems = await ctx.TempMemories.GetRecentByChannelAsync(channelId, 10);
            var recentMemContents = recentMems.Count > 0
                ? recentMems.ConvertAll(m => m.Content)
                : null;

            var contextLines = contextMessages.Select(FormatMessageLine).ToList();
            var newLines = newMessages.Select(FormatMessageLine).ToList();

            var participantNames = recentParticipants.Values
                .Select(p => p.DisplayName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct().ToList();
            if (participantNames.Count > 0)
                contextLines.Insert(0, $"[群聊参与者: {string.Join("、", participantNames)}]");

            var core = new MemoryExtractionCore();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = await core.ExtractAsync(contextLines, newLines, recentMemContents);
            sw.Stop();

            int count = 0;
            var storeResults = new List<object>();
            foreach (var item in results)
            {
                if (item.Type == "knowledge")
                {
                    await ctx.MemorySvc.StoreAsync(item.Content,
                        personId: null, channelId: null,
                        confidence: item.Confidence,
                        type: MemoryType.Knowledge, subject: item.Subject);
                    storeResults.Add(new { type = "knowledge", content = item.Content, confidence = item.Confidence, subject = item.Subject });
                }
                else if (item.Type == "feedback" && item.Sentiment != null)
                {
                    int personId = ResolveAboutToPersonId(item.About) ?? context.Person.Id;
                    await ctx.MemorySvc.ApplyFeedbackAsync(
                        personId, item.Content, item.Sentiment, item.Correction);
                    storeResults.Add(new { type = "feedback", content = item.Content, sentiment = item.Sentiment, about = item.About, personId });
                }
                else
                {
                    int personId = ResolveAboutToPersonId(item.About) ?? context.Person.Id;
                    string memType = item.Type ?? MemoryType.Fact;
                    await ctx.MemorySvc.StoreAsync(item.Content,
                        personId, context.Channel.Id,
                        confidence: item.Confidence,
                        type: memType, subject: item.Subject);
                    storeResults.Add(new { type = memType, content = item.Content, confidence = item.Confidence, subject = item.Subject, personId });
                }
                count++;
            }

            lastExtractedMessageId = newMessages[^1].Id;
            await ctx.Session.UpdateExtractionProgressAsync(channelId, lastExtractedMessageId);
            extractedMessageCount = await ctx.Session.GetMessageCountUpToAsync(
                channelId, lastExtractedMessageId);

            batchSpan.SetCloseDetail(new
            {
                extractionCount = count,
                elapsed_ms = sw.ElapsedMilliseconds,
                extractedMessageId = lastExtractedMessageId,
                progress = $"{extractedMessageCount}/{totalMessageCount}",
                // FULL extraction results
                extractions = results.Select(r => new
                {
                    type = r.Type,
                    content = r.Content,               // FULL content
                    confidence = r.Confidence,
                    subject = r.Subject,
                    sentiment = r.Sentiment,
                    about = r.About,
                    correction = r.Correction
                }).ToList(),
                storeResults,
                // Context lines fed to extraction core
                contextLines,                            // FULL context lines
                newLines                                 // FULL new lines
            });

            batchResults.Add(new
            {
                batchIndex,
                extractionCount = count,
                newMessageCount = newMessages.Count,
                lastMessageId = newMessages[^1].Id
            });

            batchIndex++;
            if (count > 0) { /* existing nop */ }
            if (newMessages.Count < 50) break;
        }

        extractSpan.SetCloseDetail(new
        {
            completed = true,
            totalBatches = batchResults.Count,
            batches = batchResults,
            finalExtractedMessageId = lastExtractedMessageId,
            finalProgress = $"{extractedMessageCount}/{totalMessageCount}"
        });
    }
    catch (Exception ex)
    {
        Signal.Error(LogGroup.Memory, "记忆提取失败",
            new
            {
                channelId,
                error = ex.GetType().Name,
                message = ex.Message,
                stack = ex.StackTrace,
                lastExtractedMessageId,
                batchesCompleted = batchResults.Count,
                batchResults
            });
        extractSpan.SetCloseDetail(new
        {
            completed = false,
            error = ex.GetType().Name,
            message = ex.Message,
            batchesCompleted = batchResults.Count
        });
    }
    finally
    {
        extractionRunning = false;
    }
}
```

### 10. Add Mode Transition Events

**Location:** `PrepareContextAsync` L584-588 (Express→Working escalation) and `DecideNext` (L912-967)

```csharp
// In PrepareContextAsync, when detecting task and switching to working:
if (!isWorkingMode)
{
    var isTask = await preprocessingCore.IsTaskAsync(
        batch.Select(b => b.Message.Content).LastOrDefault() ?? "");
    if (isTask)
    {
        isWorkingMode = true;
        consecutiveExternalTriggers = 0;

        Signal.Event(LogGroup.Engine, "模式切换: Express→Working",
            new
            {
                channelId,
                trigger = "task_detected",
                messageContent = batch.Select(b => b.Message.Content).LastOrDefault(),
                participantCount = recentParticipants.Count
            });
    }
}
```

In `EndWorkingSession` (L969-972):

```csharp
private void EndWorkingSession()
{
    Signal.Event(LogGroup.Engine, "模式切换: Working→Express",
        new
        {
            channelId,
            totalRounds = loopControlModule.TotalRounds,
            silentRounds = loopControlModule.SilentRounds,
            hadSpeakThisRound = speakModule.HadSpeakThisRound,
            isMaxSilentReached = loopControlModule.IsMaxSilentReached,
            isMaxRoundsReached = loopControlModule.IsMaxRoundsReached,
            lastRoundToolCount = lastRoundCalls?.Count ?? 0
        });

    isInWorkingSession = false;
}
```

In `DecideNext` — escalate decision (L917-926):

```csharp
if (output.HasToolCalls && output.ToolCalls!.Any(c => c.Tool == "escalate"))
{
    var call = output.ToolCalls!.First(c => c.Tool == "escalate");
    escalationReason = call.Inputs.Count > 0 && !string.IsNullOrWhiteSpace(call.Inputs[0])
        ? call.Inputs[0] : null;

    Signal.Event(LogGroup.Engine, "模式切换: Express→Working",
        new
        {
            channelId,
            trigger = "escalate_tool",
            escalationReason,
            participantCount = recentParticipants.Count
        });

    isWorkingMode = true;
    isInWorkingSession = true;
    consecutiveExternalTriggers = 0;
    gate.Signal();
}
```

### 11. Add Watch Rule Hit Event

**Location:** `CheckWatchRulesAsync` (L1450-1510)

```csharp
if (!matched) continue;

Signal.Event(LogGroup.Engine, "关注规则命中",
    new
    {
        channelId,
        channelName,
        ruleId = rule.RuleId,
        description = rule.Description,
        pattern = rule.Pattern,
        action = rule.Action.ToString(),
        autoResponse = rule.AutoResponse,
        matchedContent = msg.Content,         // FULL matched message
        matchedContentPreview = msg.Content?.Length > 50
            ? msg.Content[..50] + "…" : msg.Content,
        senderName = sc.Person?.Name ?? sc.User?.PlatformId,
        messageId = msg.MessageId
    });

// ... rest of rule handling ...
```

### 12. Add Trust Progress Event

**Location:** `IncrementDailyProgressAsync` (L1387-1406)

```csharp
private async Task IncrementDailyProgressAsync(Person person)
{
    var cfg = ctx.TrustConfig;
    var today = DateTime.Today;
    var personId = person.Id;

    float oldProgress = person.TrustProgress;
    float oldAccumulated = 0;

    if (dailyProgressTracker.TryGetValue(personId, out var entry) && entry.Date == today)
    {
        if (entry.Accumulated >= cfg.DailyInteractionCap) return;
        oldAccumulated = entry.Accumulated;
        var newAcc = entry.Accumulated + cfg.DailyInteractionIncrement;
        dailyProgressTracker[personId] = (today, newAcc);
    }
    else
    {
        dailyProgressTracker[personId] = (today, cfg.DailyInteractionIncrement);
    }

    person.TrustProgress += cfg.DailyInteractionIncrement;
    await ctx.Session.UpdatePersonAsync(person);

    Signal.Debug(LogGroup.Engine, "信任进度更新",
        new
        {
            personId,
            personName = person.Name,
            channelId,
            oldProgress,
            newProgress = person.TrustProgress,
            increment = cfg.DailyInteractionIncrement,
            dailyAccumulated = dailyProgressTracker[personId].Accumulated,
            dailyCap = cfg.DailyInteractionCap
        });
}
```

### 13. Add Image Processing Span

**Location:** `ResolveImagePresentationAsync` (L1418-1427)

```csharp
private Task<List<ImageEmbed>?> ResolveImagePresentationAsync(
    List<(string Path, string? Hash, string? Category)> images)
{
    using var imgSpan = Signal.Open(LogGroup.Engine, "图片解析",
        new
        {
            channelId,
            imageCount = images.Count,
            images = images.Select(i => new
            {
                path = i.Path,
                hash = i.Hash,
                category = i.Category
            }).ToList()
        });

    foreach (var (path, hash, category) in images)
    {
        if (!string.IsNullOrEmpty(hash))
            _ = ImageStorage.IncrementSeenCountAsync(hash);
    }

    imgSpan.SetCloseDetail(new
    {
        imageCount = images.Count,
        hashes = images.Where(i => !string.IsNullOrEmpty(i.Hash))
            .Select(i => i.Hash).ToList()
    });

    return Task.FromResult<List<ImageEmbed>?>(null);
}
```

### 14. Enhance Tool Execution Span — Per-Tool Results

**Location:** L888-897

The current batch tool span is OK but thin. Enhance with per-tool details:

```csharp
List<ToolResult> results;
using (var toolSpan = Signal.Open(LogGroup.Tool, "执行工具",
    new
    {
        toolCount = toolCalls.Count,
        tools = string.Join(",", toolCalls.Select(c => c.Tool)),
        isWorkingSession = isInWorkingSession,
        roundNumber = loopControlModule.TotalRounds,
        // FULL tool calls with inputs
        toolCalls = toolCalls.Select(tc => new
        {
            tool = tc.Tool,
            inputs = tc.Inputs,                    // FULL inputs
            inputCount = tc.Inputs.Count
        }).ToList()
    }))
{
    results = await executor.ExecuteAsync(toolCalls);
    toolSpan.SetCloseDetail(new
    {
        successCount = results.Count(r => r.Status == "ok"),
        errorCount = results.Count(r => r.Error != null),
        // FULL per-tool results
        results = results.Select((r, i) => new
        {
            tool = i < toolCalls.Count ? toolCalls[i].Tool : "unknown",
            status = r.Status,
            success = r.Status == "ok",
            error = r.Error,
            outputLength = r.Data?.Length ?? 0,
            output = r.Data                        // FULL output (may be large)
        }).ToList()
    });
}
```

### 15. Add Alert Handling Span

**Location:** `HandleAlertAsync` (L1384-1385)

```csharp
private async Task HandleAlertAsync(Person person, SessionContext sc, string reason)
{
    using var alertSpan = Signal.Open(LogGroup.Engine, "触发警报",
        new
        {
            personId = person.Id,
            personName = person.Name,
            channelId = sc.Channel.Id,
            channelName = sc.Channel.Name,
            reason
        });

    await AlertHandler.HandleAsync(person, sc, reason, ctx);

    alertSpan.SetCloseDetail(new { handled = true });
}
```

### 16. Add Error Events in Empty Catch Blocks

**Location:** Multiple catch blocks that silently swallow errors

**GetCachedMemoryAsync** (L1065-1068):
```csharp
catch (Exception ex)
{
    Signal.Warn(LogGroup.Memory, "记忆缓存获取失败",
        new
        {
            personId,
            error = ex.GetType().Name,
            message = ex.Message
        });
    return new List<ScoredMemory>();
}
```

**GetCachedQueryIntentAsync** (L1124-1127):
```csharp
catch (Exception ex)
{
    Signal.Warn(LogGroup.Engine, "查询意图缓存获取失败",
        new
        {
            channelId,
            error = ex.GetType().Name,
            message = ex.Message
        });
    return null;
}
```

**RunExtractionAsync** (L1255-1257) — already covered in extraction span above.

**Speak OnSpeak error catch** (L437-442, the adapter send failure):
The current `OnSpeak` callback doesn't have try/catch. Add one:

```csharp
speakModule.OnSpeak = async (rawText) =>
{
    try
    {
        // ... existing code ...
    }
    catch (Exception ex)
    {
        Signal.Error(LogGroup.Adapter, "消息发送失败",
            new
            {
                channelId,
                platform = currentLastMsg?.Platform,
                rawText,
                error = ex.GetType().Name,
                message = ex.Message
            });
    }
};
```

### 17. Enhance Lifecycle Span Close Detail

**Location:** L424

```csharp
lifeCtx.Close(new
{
    engineType = EngineType,
    channelId,
    channelName,
    reason = "cold_timeout",
    totalRounds = loopControlModule.TotalRounds,
    totalErrors = totalErrorCount,
    processedMessageCount,
    extractedMessageCount,
    totalMessageCount,
    finalExtractedMessageId = lastExtractedMessageId,
    wasWorkingMode = isWorkingMode,
    wasInWorkingSession = isInWorkingSession,
    finalImpulse = impulseTracker.Impulse,
    finalParticipantCount = recentParticipants.Count,
    isAlive = IsAlive,
    lastErrorTime,
    lastErrorMessage
});
```

### 18. Enhance Session Span Close Detail

**Location:** L323 (session close on gate block) and L403 (session close on loop end)

```csharp
sessionCtx.Close(new
{
    reason = "循环挂起",
    mode = isWorkingMode ? "working" : "express",
    totalRoundsThisSession = loopControlModule.TotalRounds,  // need per-session counter
    hadSpeak = speakModule.HadSpeakThisRound,
    wasInWorkingSession = isInWorkingSession,
    messagesInBatch = batch?.Count ?? 0,
    consecutiveFailures,
    consecutiveExternalTriggers
});
```

### 19. Add Poke Marker Span

**Location:** `ProcessPokeMarkers` (L1323-1342)

```csharp
private async Task<string> ProcessPokeMarkers(string text, IncomingMessage lastMsg)
{
    var matches = PokeRegex.Matches(text);
    if (matches.Count == 0) return text;

    using var pokeSpan = Signal.Open(LogGroup.Adapter, "执行戳一戳",
        new
        {
            platform = lastMsg.Platform,
            channelId = lastMsg.ChannelId,
            pokeCount = matches.Count,
            targetUids = matches.Select(m => m.Groups[1].Value).ToList()
        });

    foreach (Match match in matches)
    {
        var targetUid = match.Groups[1].Value;
        long? groupId = lastMsg.ChannelId.StartsWith("group_")
            ? long.Parse(lastMsg.ChannelId[6..])
            : null;

        var parameters = new Dictionary<string, string> { ["user_id"] = targetUid };
        if (groupId.HasValue) parameters["group_id"] = groupId.Value.ToString();

        var result = await ctx.Adapters.ExecuteActionAsync(lastMsg.Platform, lastMsg.ChannelId, "poke", parameters);
    }

    pokeSpan.SetCloseDetail(new
    {
        pokeCount = matches.Count,
        targets = matches.Select(m => m.Groups[1].Value).ToList()
    });

    return PokeRegex.Replace(text, "").Trim();
}
```

### 20. Add Context Compression Decision Event

**Location:** `BuildPromptMessages` — after context builder, if compression happened

If ContextBuilder has compression info, add:

```csharp
// After contextBuilder.BuildContextXmlAsync in AssembleRoundContextAsync:
if (contextBuilder.LastCompression != null)
{
    Signal.Event(LogGroup.Engine, "上下文压缩",
        new
        {
            channelId,
            originalTokens = contextBuilder.LastCompression.OriginalTokens,
            compressedTokens = contextBuilder.LastCompression.CompressedTokens,
            roundsDropped = contextBuilder.LastCompression.RoundsDropped,
            compressionRatio = contextBuilder.LastCompression.Ratio
        });
}
```

---

## Naming Conventions

| Event | Name | Group |
|-------|------|-------|
| Lifecycle | `Channel引擎 [{channelName}]` | Engine |
| Session | `频道会话` | Engine |
| Gate evaluation | `闸门评估` | Engine |
| Round | `处理轮次` | Engine |
| Context assembly | `组装对话上下文` | Memory |
| Model call | `AI模型调用` | Model |
| Tool execution | `执行工具` | Tool |
| Message send | `发送消息` | Adapter |
| Media send | `发送媒体` | Adapter |
| Multi-segment send | `分条发送消息` | Adapter |
| Message enqueue | `消息入队` (event) | Adapter |
| Poke | `执行戳一戳` | Adapter |
| Impulse decision | `冲动值决策` (event/debug) | Engine |
| Mode switch | `模式切换: Express→Working` (event) | Engine |
| Mode switch | `模式切换: Working→Express` (event) | Engine |
| Interceptor chain | `拦截器链` | Engine |
| Memory fetch | `获取频道记忆` | Memory |
| Memory extraction task | `记忆提取任务` | Memory |
| Memory extraction batch | `提取批次` | Memory |
| Watch rule match | `关注规则命中` (event) | Engine |
| Trust progress | `信任进度更新` (debug) | Engine |
| Image processing | `图片解析` | Engine |
| Alert | `触发警报` | Engine |
| Context compression | `上下文压缩` (event) | Engine |
| Processing error | `处理异常` (error) | Engine |
| Memory cache error | `记忆缓存获取失败` (warn) | Memory |
| Intent cache error | `查询意图缓存获取失败` (warn) | Engine |
| Message send error | `消息发送失败` (error) | Adapter |
| Memory extraction error | `记忆提取失败` (error) | Memory |

---

## Special Considerations

### 1. Full Content Logging

Unlike other modules where content should be truncated for privacy/size, ChannelEngine is the core interaction loop. Per the user's explicit requirement:

- **Model prompt messages:** Log FULL content of every message in `BuildPromptMessages` output — every role, every content field, every tool description, every injection
- **Model output:** Log FULL response text and FULL tool call parameters
- **Message send:** Log FULL content before and after parsing
- **Message receive:** Log FULL incoming message content
- **Memory results:** Log FULL memory content, scores, types
- **Extraction input/output:** Log FULL conversation lines fed to extraction core and FULL extraction results

### 2. Token/Performance Data

The `ModelOutput` class may not currently have token fields. The ModelCallLog table already tracks tokens. Either:
- Add `InputTokens`/`OutputTokens`/`FinishReason`/`ModelUsed` properties to `ModelOutput`
- Or read from ModelCallLog after the call and include in Signal detail

### 3. AgentCore Properties

If `agentCore.CurrentModel` and `agentCore.CurrentCore` don't exist, add them as public get-only properties. These are useful for debugging model-specific behavior.

### 4. Per-Session Round Counter

The session close detail references `totalRoundsThisSession`. This doesn't exist yet — `loopControlModule.TotalRounds` is the lifetime counter. Either:
- Add a `SessionRounds` counter that resets when session closes
- Or snapshot `TotalRounds` at session start and compute delta at close

### 5. Signal Volume

ChannelEngine is the highest-frequency code path. With all these spans, a single express round will produce:
- 1x message enqueue event
- 1x gate evaluation span
- 1x impulse decision event (debug)
- 1x context assembly span
- 1x memory fetch span
- 1x model call span
- 1x message send span
- 1x round span
- 1x trust progress event (debug)

This is ~10 Signal writes per express round. At peak chat activity (~20 rounds/minute), that's ~200 writes/minute — negligible for SQLite.

### 6. Context Restoration After Session Change

Already correct at L323-325 and L403-407. Maintain this pattern.

---

## Checklist

- [ ] Model call span includes: full prompt messages, full output text, full tool calls, token usage, timing, model/core name
- [ ] Message send span (OnSpeak) includes: full rawText, full content, replyTo, mentions, timing, sentId
- [ ] Message send span (OnSendMedia) includes: media type, full text, attachment paths, timing, sentId
- [ ] SendSegmentsAsync wrapped in span with full text, segment list, sentIds
- [ ] EnqueueMessage emits "消息入队" event with full content, sender, channel, attachments
- [ ] Gate evaluation span includes: impulse, threshold, mute state, sleep state, batch messages, interceptor results
- [ ] Impulse decision event for skip/proceed decisions
- [ ] Interceptor chain span with per-interceptor name, action, injections
- [ ] Context assembly span close includes: full context XML, full memory results, full recent messages
- [ ] Memory fetch span includes: full query, intent keywords/subjects, full results with scores
- [ ] Memory extraction task span + per-batch spans with full messages, full extractions, full store results, progress
- [ ] Mode transition events for Express↔Working switches (task_detected, escalate_tool, session_end triggers)
- [ ] Watch rule hit events with full matched content and rule details
- [ ] Trust progress debug events
- [ ] Image processing span
- [ ] Alert handling span
- [ ] Poke marker span
- [ ] Lifecycle close detail includes: totalRounds, totalErrors, processedCount, extractedCount, final state
- [ ] Session close detail includes: rounds this session, mode, messages, tool results
- [ ] Error events in all catch blocks (memory cache, intent cache, message send, extraction)
- [ ] All Signal names are in Chinese
- [ ] All detail fields use English keys
- [ ] Model input/output logged IN FULL (no truncation)
- [ ] Message content logged IN FULL (no truncation)
- [ ] Memory content logged IN FULL (no truncation)
- [ ] Build succeeds
- [ ] `using System.Diagnostics` added where Stopwatch is needed

---

## Files Touched (Single File)

| Action | File | Sections |
|--------|------|----------|
| Modify | `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs` | Constructor, EnqueueMessage, RunAsync, WireModuleCallbacks, PrepareContextAsync, AssembleRoundContextAsync, BuildPromptMessages, ProcessResponseAsync, DecideNext, EndWorkingSession, GetCachedMemoryAsync, FetchMemoryAsync, RunExtractionAsync, SendSegmentsAsync, ProcessPokeMarkers, ResolveImagePresentationAsync, IncrementDailyProgressAsync, CheckWatchRulesAsync |

**No new files needed.**

---

## Potential Additions to AgentCore / ModelOutput

These are small prerequisite changes that the ChannelEngine guide depends on:

| Class | Add |
|-------|-----|
| `ModelOutput` | `InputTokens?`, `OutputTokens?`, `FinishReason?`, `ModelUsed?` properties |
| `AgentCore` | `CurrentModel` (public get), `CurrentCore` (public get) properties |
