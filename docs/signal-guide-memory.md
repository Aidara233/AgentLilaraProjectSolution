# Signal Guide: Memory Services

> **Status:** Zero instrumentation across all memory-related files.
> **Goal:** Add Memory-group spans for store/recall/extraction/query operations with full context.
> **Reference:** `signal-instrumentation-guide.md` for API conventions.

## Files Covered

| File | Lines | Key Methods | Group |
|------|-------|------------|-------|
| `AgentCoreProcessor/Memory/MemoryService.cs` | ~363 | `StoreAsync`, `RecallAsync` (×2), `ForgetAsync`, `ApplyFeedbackAsync` | Memory |
| `AgentCoreProcessor/Core/MemoryExtractionCore.cs` | ~128 | `ExtractAsync` (×2), `ParseResults` | Engine |
| `AgentCoreProcessor/Core/MemoryQueryCore.cs` | ~55 | `ExtractIntentAsync`, `ParseIntent` | Engine |

**Important:** All Signal names in the code must be in Chinese. Detail fields use English keys.
**Detail standard:** Full content in all details — no truncation. Full memory content (store/recall/extraction), full model output, full raw text. Only exclude binary data and secrets.

---

## MemoryService

**File:** `AgentCoreProcessor/Memory/MemoryService.cs`

### 1. Add span for StoreAsync

At L52, wrap the store operation:

```csharp
public async Task<TempMemoryEntry> StoreAsync(
    string content,
    int? personId = null, int? channelId = null,
    int? sourceMessageId = null, string confidence = "high",
    string type = MemoryType.Fact, string? subject = null)
{
    using var storeSpan = Signal.Open(LogGroup.Memory, "记忆存储",
        new
        {
            type,
            confidence,
            hasSubject = subject != null,
            hasPersonId = personId != null,
            hasChannelId = channelId != null,
            contentLength = content.Length,
            content = content,                     // FULL content
            subject
        });

    var sw = System.Diagnostics.Stopwatch.StartNew();
    byte[]? embeddingBytes = null;
    try
    {
        var vec = await embedding.GetEmbeddingAsync(content);
        embeddingBytes = SiliconFlowEmbeddingProvider.FloatsToBytes(vec);
    }
    catch (Exception ex)
    {
        Signal.Warn(LogGroup.Memory, "Embedding生成失败", new
        {
            contentLength = content.Length,
            error = ex.GetType().Name,
            message = ex.Message
        });
    }
    sw.Stop();

    var result = await tempMemories.CreateAsync(
        content, embeddingBytes, personId, channelId, sourceMessageId, confidence, type, subject);

    storeSpan.SetCloseDetail(new
    {
        memoryId = result.Id,
        hasEmbedding = embeddingBytes != null,
        embeddingSize = embeddingBytes?.Length,
        elapsed_ms = sw.ElapsedMilliseconds,
        type,
        confidence,
        content = content                      // FULL stored content
    });
    return result;
}
```

### 2. Add span for RecallAsync (main version)

At L76, wrap the recall operation:

```csharp
public async Task<List<ScoredMemory>> RecallAsync(
    int personId, int channelId,
    string query, int topK = 10, bool includeLinks = true, bool includePersona = false)
{
    using var recallSpan = Signal.Open(LogGroup.Memory, "记忆检索",
        new
        {
            personId,
            channelId,
            query,                                 // FULL query
            queryLength = query.Length,
            topK,
            includeLinks,
            includePersona
        });

    var sw = System.Diagnostics.Stopwatch.StartNew();
    float[]? queryVec = null;
    try
    {
        queryVec = await embedding.GetEmbeddingAsync(query);
    }
    catch (Exception ex)
    {
        Signal.Warn(LogGroup.Memory, "查询Embedding生成失败", new
        {
            queryLength = query.Length,
            error = ex.GetType().Name
        });
    }

    // ... existing scoring and search logic ...

    sw.Stop();

    // After result is computed, log close detail:
    recallSpan.SetCloseDetail(new
    {
        resultCount = result.Count,
        tempResults = scored.Count(s => s.IsTemp),
        mainResults = scored.Count(s => !s.IsTemp && !s.IsPersona),
        personaResults = scored.Count(s => s.IsPersona),
        topScore = result.FirstOrDefault()?.Score,
        elapsed_ms = sw.ElapsedMilliseconds,
        hadEmbedding = queryVec != null,
        // FULL recall results
        results = result.Select(m => new
        {
            id = m.Id,
            content = m.Content,               // FULL content
            score = m.Score,
            confidence = m.Confidence,
            type = m.Type,
            subject = m.Subject,
            isTemp = m.IsTemp,
            isPersona = m.IsPersona
        }).ToList()
    });

    return result;
}
```

### 3. Add span for RecallAsync (with intent)

At L223, wrap the enhanced recall:
```csharp
using var recallSpan = Signal.Open(LogGroup.Memory, "记忆检索 [增强]",
    new
    {
        personId,
        channelId,
        queryLength = query.Length,
        keywordCount = intent.Keywords.Count,
        subjectCount = intent.Subjects.Count,
        topK,
        includeLinks,
        includePersona
    });
// ... same pattern as above ...
```

### 4. Add span for ForgetAsync

At L274:
```csharp
public async Task ForgetAsync(int memoryId)
{
    using var forgetSpan = Signal.Open(LogGroup.Memory, "记忆删除",
        new { memoryId });

    var memory = await memories.GetByIdAsync(memoryId);
    if (memory != null)
    {
        await memories.DeleteAsync(memory);
        forgetSpan.SetCloseDetail(new
        {
            deleted = true,
            content = memory.Content,           // FULL deleted content
            type = memory.Type,
            confidence = memory.Confidence
        });
    }
    else
    {
        forgetSpan.SetCloseDetail(new { deleted = false, reason = "not_found" });
    }
}
```

### 5. Add span for ApplyFeedbackAsync

At L285:
```csharp
public async Task ApplyFeedbackAsync(
    int personId, string feedbackContent,
    string sentiment, string? correction)
{
    using var feedbackSpan = Signal.Open(LogGroup.Memory, "记忆反馈",
        new
        {
            personId,
            sentiment,
            hasCorrection = correction != null,
            feedbackContent,                       // FULL feedback content
            correction
        });

    // ... existing matching logic ...

    if (bestMatch != null && bestSim >= bestTempSim)
    {
        // ... update main memory ...
        feedbackSpan.SetCloseDetail(new
        {
            matched = true,
            target = "main",
            memoryId = bestMatch.Id,
            similarity = bestSim,
            action = sentiment == "positive" ? "boost_confidence" : "penalize"
        });
    }
    else if (bestTempMatch != null)
    {
        // ... update or delete temp memory ...
        feedbackSpan.SetCloseDetail(new
        {
            matched = true,
            target = "temp",
            memoryId = bestTempMatch.Id,
            similarity = bestTempSim,
            action = sentiment == "positive" ? "boost_confidence" : "delete"
        });
    }
    else
    {
        feedbackSpan.SetCloseDetail(new
        {
            matched = false,
            reason = "no_match_above_threshold",
            bestSimilarity = Math.Max(bestSim, bestTempSim)
        });
    }
}
```

---

## MemoryExtractionCore

**File:** `AgentCoreProcessor/Core/MemoryExtractionCore.cs`

This extends CoreBase, which already has model call spans. So the model layer is covered. We just need to wrap the extraction operation.

### 1. Add span for ExtractAsync (simple version, L48)

```csharp
public async Task<List<ExtractionResult>> ExtractAsync(List<string> conversationLines)
{
    using var extractSpan = Signal.Open(LogGroup.Engine, "记忆提取",
        new
        {
            lineCount = conversationLines.Count,
            totalChars = conversationLines.Sum(l => l.Length),
            mode = "simple"
        });

    ResetProcessor();
    var sb = new StringBuilder();
    sb.AppendLine("以下是一段对话：");
    foreach (var line in conversationLines)
        sb.AppendLine(line);
    var result = await GenerateOnceAsync(sb.ToString());
    var parsed = ParseResults(result);

    extractSpan.SetCloseDetail(new
    {
        extractionCount = parsed.Count,
        types = parsed.Select(e => e.Type).Distinct().ToList(),
        hasFeedback = parsed.Any(e => e.Type == "feedback"),
        rawOutputLength = result?.Length ?? 0
    });

    return parsed;
}
```

### 2. Add span for ExtractAsync (dual-phase version, L62)

```csharp
public async Task<List<ExtractionResult>> ExtractAsync(
    List<string> contextLines,
    List<string> newLines,
    List<string>? recentMemories)
{
    using var extractSpan = Signal.Open(LogGroup.Engine, "记忆提取 [双段]",
        new
        {
            contextLineCount = contextLines.Count,
            newLineCount = newLines.Count,
            recentMemoryCount = recentMemories?.Count ?? 0,
            totalChars = contextLines.Sum(l => l.Length) + newLines.Sum(l => l.Length),
            mode = "dual-phase"
        });

    // ... existing code ...

    var parsed = ParseResults(result);

    extractSpan.SetCloseDetail(new
    {
        extractionCount = parsed.Count,
        types = parsed.Select(e => e.Type).Distinct().ToList(),
        confidences = parsed.Select(e => e.Confidence).Distinct().ToList(),
        hasFeedback = parsed.Any(e => e.Type == "feedback"),
        rawOutputLength = result?.Length ?? 0
    });

    return parsed;
}
```

### 3. Add event for no extractions

In `ParseResults`, when nothing is extracted, add a debug event:
```csharp
if (parsed.Count == 0)
{
    Signal.Debug(LogGroup.Engine, "记忆提取: 无结果", new
    {
        rawOutput = trimmed                     // FULL raw output
    });
}
```

### 4. Add warn event for parse failures

In the JSON parse catch block (L112):
```csharp
catch (Exception ex)
{
    Signal.Warn(LogGroup.Engine, "记忆提取JSON解析失败", new
    {
        rawOutput = trimmed,                    // FULL raw output
        rawOutputLength = trimmed.Length,
        error = ex.GetType().Name
    });
}
```

---

## MemoryQueryCore

**File:** `AgentCoreProcessor/Core/MemoryQueryCore.cs`

### 1. Add span for ExtractIntentAsync

```csharp
public async Task<MemoryQueryIntent> ExtractIntentAsync(List<string> recentMessages)
{
    using var intentSpan = Signal.Open(LogGroup.Engine, "记忆查询意图提取",
        new
        {
            messageCount = recentMessages.Count,
            totalChars = recentMessages.Sum(m => m.Length),
            messages = recentMessages               // FULL messages
        });

    ResetProcessor();
    var sb = new StringBuilder();
    sb.AppendLine("以下是最近的对话：");
    foreach (var line in recentMessages)
        sb.AppendLine(line);

    var result = await GenerateOnceAsync(sb.ToString());
    var intent = ParseIntent(result);

    intentSpan.SetCloseDetail(new
    {
        keywordCount = intent.Keywords.Count,
        subjectCount = intent.Subjects.Count,
        hasKeywords = intent.Keywords.Count > 0,
        hasSubjects = intent.Subjects.Count > 0,
        rawOutputLength = result?.Length ?? 0
    });

    return intent;
}
```

### 2. Add warn event for parse failures

In `ParseIntent` catch block (L49):
```csharp
catch (Exception ex)
{
    Signal.Warn(LogGroup.Engine, "意图提取JSON解析失败", new
    {
        rawOutput = trimmed,                     // FULL raw output
        rawOutputLength = trimmed.Length,
        error = ex.GetType().Name
    });
    return new();
}
```

---

## Important Notes

1. **MemoryExtractionCore and MemoryQueryCore inherit from CoreBase.** CoreBase already logs model calls with `Signal.Open(LogGroup.Model, ...)` and `Signal.Debug("模型请求发出")`. The spans added here wrap at the **operation level** (extraction, query intent) and the model call spans will nest inside automatically. Ensure CoreBase model spans also log full prompt and full output.

2. **Log full memory content** in all detail fields — no truncation. Store, recall, extraction results should all include complete content.

3. **Log full model output** in extraction/intent spans — the raw model response text is essential for debugging parse failures and output quality.

4. **Use `Signal.Debug` for no-result cases** — these are high-frequency but useful for debugging.

5. **Use `Signal.Warn` for parse failures** — recoverable but indicate model output quality issues. Include full raw output that failed to parse.

---

## Naming Conventions

| Operation | Name | Group |
|-----------|------|-------|
| Store memory | `记忆存储` | Memory |
| Recall memory | `记忆检索` | Memory |
| Enhanced recall | `记忆检索 [增强]` | Memory |
| Delete memory | `记忆删除` | Memory |
| Feedback | `记忆反馈` | Memory |
| Extraction (simple) | `记忆提取` | Engine |
| Extraction (dual) | `记忆提取 [双段]` | Engine |
| Intent extraction | `记忆查询意图提取` | Engine |
| No extraction result | `记忆提取: 无结果` (debug) | Engine |
| Embedding failure | `Embedding生成失败` (warn) | Memory |
| Parse failure | `记忆提取JSON解析失败` (warn) | Engine |

---

## Checklist

- [ ] MemoryService.StoreAsync wrapped in `Signal.Open(LogGroup.Memory, ...)`
- [ ] MemoryService.RecallAsync (both overloads) wrapped in Memory spans
- [ ] MemoryService.ForgetAsync wrapped in Memory span
- [ ] MemoryService.ApplyFeedbackAsync wrapped in Memory span
- [ ] MemoryExtractionCore.ExtractAsync (both overloads) wrapped in Engine spans
- [ ] MemoryQueryCore.ExtractIntentAsync wrapped in Engine span
- [ ] All spans have close detail with counts, timing, outcomes, and full data
- [ ] Warning events for embedding failures, parse failures (with full raw output)
- [ ] Debug events for no-result cases (with full raw output)
- [ ] Full memory content in all details (no truncation)
- [ ] Full model output in extraction/intent spans (no truncation)
- [ ] All names are in Chinese, descriptive
- [ ] `using System.Diagnostics` added where Stopwatch is needed
