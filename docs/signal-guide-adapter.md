# Signal Guide: AdapterManager

> **Status:** ~20% complete. Has send/receive spans, missing lifecycle and events.
> **Goal:** Add lifecycle span for StartAll/StopAll, connection state events, error events.
> **Reference:** `signal-instrumentation-guide.md` for API conventions.

**File:** `AgentCoreProcessor/Adapter/AdapterManager.cs`

**Important:** All Signal names in the code must be in Chinese. Detail fields use English keys.
**Detail standard:** Full content in all details — no truncation. Full message content in receive/send spans, full timing data, full error context. Only exclude binary data and secrets.

---

## Current State

Already present:
- `Signal.Begin(LogGroup.Adapter, ...)` on message receive (L94-99) — correct
- `Signal.Open(LogGroup.Adapter, "消息发送", ...)` on send (L184-191) — correct
- `Signal.Open(LogGroup.Adapter, "消息发送", ...)` on send by ID (L197-205) — correct

Missing:
- **Lifecycle span** for LoadFromConfig / StartAll / StopAll
- **Connection state events** (adapter started, stopped, enabled, disabled)
- **Error events** in catch blocks
- **Close detail** on message spans

---

## Changes Needed

### 1. Add lifecycle span for StartAllAsync

Wrap the batch start in a lifecycle span:

```csharp
public async Task StartAllAsync(bool debugMode = false, CancellationToken ct = default)
{
    var parentCtx = SignalContext.Current;
    var startCtx = Signal.Continue(
        parentCtx?.SignalId ?? Signal.NewId(),
        parentCtx?.CurrentSpanId,
        "system:init",
        LogGroup.Adapter,
        "适配器启动",
        new
        {
            totalAdapters = adapters.Count,
            debugMode,
            autoStartCount = adapters.Count(kv =>
                kv.Value != null &&
                enabledMap.GetValueOrDefault(kv.Key, true) &&
                (!configs.TryGetValue(kv.Key, out var cfg) || cfg.AutoStart))
        });

    try
    {
        var tasks = adapters
            .Where(kv =>
            {
                if (!enabledMap.GetValueOrDefault(kv.Key, true)) return false;
                if (!configs.TryGetValue(kv.Key, out var cfg)) return true;
                if (!cfg.AutoStart) return false;
                if (debugMode && !cfg.AutoStartDebug) return false;
                return true;
            })
            .Select(kv => kv.Value.StartAsync(ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        startCtx.Close(new
        {
            startedCount = tasks.Count(),
            reason = "completed"
        });
    }
    catch (Exception ex)
    {
        Signal.Error(LogGroup.Adapter, "适配器启动失败", new
        {
            error = ex.GetType().Name,
            message = ex.Message
        });
        startCtx.Close(new { reason = "failed", error = ex.Message });
    }
}
```

### 2. Add events for individual adapter lifecycle

**In RegisterAdapter** (around L89), add an event when an adapter is registered:
```csharp
public void RegisterAdapter(IAdapter adapter, bool enabled = true)
{
    adapter.OnMessageReceived += msg => { /* existing */ };
    adapters[adapter.Id] = adapter;
    enabledMap[adapter.Id] = enabled;

    Signal.Event(LogGroup.Adapter, "适配器注册", new
    {
        adapterId = adapter.Id,
        platform = adapter.Platform,
        type = adapter.GetType().Name,
        enabled
    });
}
```

**In AddAsync** (L108), add events for dynamic add:
```csharp
public async Task<bool> AddAsync(AdapterInstanceConfig config)
{
    if (adapters.ContainsKey(config.Id)) return false;

    configs[config.Id] = config;
    var adapter = AdapterFactory.Create(config);
    RegisterAdapter(adapter, config.Enabled);
    SaveConfig(config);

    if (config.Enabled)
    {
        Signal.Event(LogGroup.Adapter, "适配器启动", new
        {
            adapterId = config.Id,
            platform = adapter.Platform,
            type = config.Type
        });
        await adapter.StartAsync();
    }

    OnAdaptersChanged?.Invoke();
    return true;
}
```

**In RemoveAsync** (L124), add stop event:
```csharp
public async Task<bool> RemoveAsync(string id)
{
    if (!adapters.TryRemove(id, out var adapter)) return false;
    enabledMap.TryRemove(id, out _);
    configs.TryRemove(id, out _);

    Signal.Event(LogGroup.Adapter, "适配器移除", new
    {
        adapterId = id,
        platform = adapter.Platform
    });

    await adapter.StopAsync();
    // ... delete config file ...
    OnAdaptersChanged?.Invoke();
    return true;
}
```

**In EnableAsync** (L139):
```csharp
public async Task<bool> EnableAsync(string id)
{
    if (!adapters.TryGetValue(id, out var adapter)) return false;
    enabledMap[id] = true;
    UpdateConfigEnabled(id, true);

    Signal.Event(LogGroup.Adapter, "适配器启用", new
    {
        adapterId = id,
        platform = adapter.Platform
    });

    await adapter.StartAsync();
    return true;
}
```

**In DisableAsync** (L148):
```csharp
public async Task<bool> DisableAsync(string id)
{
    if (!adapters.TryGetValue(id, out var adapter)) return false;
    enabledMap[id] = false;
    UpdateConfigEnabled(id, false);

    Signal.Event(LogGroup.Adapter, "适配器禁用", new
    {
        adapterId = id,
        platform = adapter.Platform
    });

    await adapter.StopAsync();
    return true;
}
```

### 3. Add stop event

**In StopAllAsync** (L172):
```csharp
public async Task StopAllAsync()
{
    Signal.Event(LogGroup.Adapter, "适配器关闭", new
    {
        adapterCount = adapters.Count,
        adapterIds = adapters.Keys.ToList()
    });

    var tasks = adapters.Values.Select(a => a.StopAsync());
    await Task.WhenAll(tasks).ConfigureAwait(false);
}
```

### 4. Enhance message receive span with close detail

The existing receive span at L94 uses `using var ctx = Signal.Begin(...)` which creates a scope, not a span. Since this is a root signal for downstream processing, the `ctx` should be closed when downstream processing finishes. But we can't close it here — downstream ChannelEngine processing may happen later.

Instead, add the message content metadata to the Signal.Begin detail (already partially done) and add a point event for receive completion:

```csharp
adapter.OnMessageReceived += msg =>
{
    msg.AdapterId = adapter.Id;
    using var ctx = Signal.Begin(
        LogGroup.Adapter,
        $"adapter:{adapter.Id}",
        "消息接收",
        new
        {
            adapterId = adapter.Id,
            platform = adapter.Platform,
            channelId = msg.ChannelId,
            userId = msg.PlatformUserId,
            isPrivate = msg.IsPrivate,
            isMentioned = msg.IsMentioned,
            content = msg.Content,             // FULL content
            contentLength = msg.Content?.Length ?? 0,
            hasAttachments = msg.Attachments?.Count > 0,
            attachmentCount = msg.Attachments?.Count ?? 0,
            attachmentTypes = msg.Attachments?.Select(a => a.Type.ToString()).ToList(),
            messageId = msg.MessageId,
            timestamp = msg.Time
        }
    );
    eventBus?.PublishMessage(msg);
};
```

### 5. Enhance message send span close detail

The existing send spans have `span.SetCloseDetail(new { messageId = result })`. Enhance them:

```csharp
public async Task<string?> SendMessageAsync(string platform, OutgoingMessage message)
{
    var adapter = ResolveForChannel(platform, message.ChannelId);
    if (adapter == null)
    {
        Signal.Warn(LogGroup.Adapter, "消息发送: 无可用适配器", new
        {
            platform,
            channelId = message.ChannelId
        });
        return null;
    }

    using var span = Signal.Open(
        LogGroup.Adapter,
        "消息发送",
        new
        {
            adapterId = adapter.Id,
            platform,
            channelId = message.ChannelId,
            content = message.Content,             // FULL content
            contentLength = message.Content?.Length ?? 0,
            hasMedia = !string.IsNullOrEmpty(message.MediaPath),
            mediaPath = message.MediaPath,
            replyTo = message.ReplyTo,
            mentions = message.Mentions
        }
    );

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await adapter.SendMessageAsync(message);
    sw.Stop();

    span.SetCloseDetail(new
    {
        messageId = result,
        success = result != null,
        elapsed_ms = sw.ElapsedMilliseconds,
        content = message.Content              // FULL sent content
    });
    return result;
}
```

Same pattern for `SendMessageByIdAsync`.

### 6. Add error events in empty catch blocks

**LoadFromConfig** catch blocks (L54, L83):
```csharp
catch (Exception ex)
{
    Signal.Warn(LogGroup.Adapter, "适配器配置加载失败", new
    {
        file = Path.GetFileName(file),
        error = ex.GetType().Name,
        message = ex.Message
    });
}
```

**ReloadAdapterAsync** — should emit event on reload:
```csharp
public async Task<bool> ReloadAdapterAsync(string idOrPlatform)
{
    var adapter = adapters.GetValueOrDefault(idOrPlatform) ?? GetAdapter(idOrPlatform);
    if (adapter == null) return false;

    Signal.Event(LogGroup.Adapter, "适配器重载配置", new
    {
        adapterId = adapter.Id,
        platform = adapter.Platform
    });

    await adapter.ReloadConfigAsync();
    return true;
}
```

---

## Naming Conventions

| Event | Name | Group |
|-------|------|-------|
| Startup | `适配器启动` | Adapter |
| Shutdown | `适配器关闭` | Adapter |
| Register | `适配器注册` (event) | Adapter |
| Start single | `适配器启动` (event) | Adapter |
| Stop/remove | `适配器移除` (event) | Adapter |
| Enable | `适配器启用` (event) | Adapter |
| Disable | `适配器禁用` (event) | Adapter |
| Reload config | `适配器重载配置` (event) | Adapter |
| Message receive | `消息接收` (signal root) | Adapter |
| Message send | `消息发送` (span) | Adapter |
| Config load error | `适配器配置加载失败` (warn) | Adapter |

---

## Special Considerations

1. **Signal.Begin for message receive is a root signal.** It creates the causality chain that ChannelEngine's `Signal.Continue` links to. The receive context must stay open until downstream processing naturally closes it — don't add an explicit close here.

2. **StartAllAsync uses `Signal.Continue`** from the startup signal (set in Program.cs), maintaining the init trace chain: Program → MasterEngine.InitAsync → AdapterManager.StartAllAsync.

3. **Individual adapter start/stop events** are point events, not spans, because the actual start/stop work happens inside the adapter implementation (OneBotAdapter etc.) which is outside our control.

---

## Checklist

- [ ] StartAllAsync wrapped in `Signal.Continue` + lifecycle span
- [ ] StopAllAsync emits "适配器关闭" event
- [ ] RegisterAdapter emits "适配器注册" event
- [ ] AddAsync emits "适配器启动" event
- [ ] RemoveAsync emits "适配器移除" event
- [ ] EnableAsync emits "适配器启用" event
- [ ] DisableAsync emits "适配器禁用" event
- [ ] ReloadAdapterAsync emits "适配器重载配置" event
- [ ] Message receive Signal.Begin detail enhanced with message metadata
- [ ] Message send spans enhanced with timing and success detail
- [ ] Error events in config loading catch blocks
- [ ] Warn event for no-adapter-available on send
- [ ] All names are in Chinese, descriptive
