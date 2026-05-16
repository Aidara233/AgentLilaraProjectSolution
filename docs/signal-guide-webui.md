# Signal Guide: WebUI Configuration Changes

> **Status:** Zero instrumentation. WebUI write operations have no Signal source.
> **Goal:** Add a single centralized interception point so all WebUI-originated state changes trace back to `webui:settings`.
> **Reference:** `signal-instrumentation-guide.md` for API conventions.

## Problem

WebUI configuration changes (tool disable, adapter enable, engine stop, mute toggle) currently have no Signal source. When these changes propagate through EventBus/TaskBridge to downstream engines, `SignalContext.Current` is null, so downstream modules either create orphan signals or silently lack tracing.

## Approach: Centralized Interception via `IHandleEvent`

Blazor Server's `IHandleEvent` interface lets a parent component intercept ALL child component events. By implementing it on `MainLayout.razor` (the layout for all authenticated pages), every button click, form submit, and UI interaction in config/admin pages flows through a single interception point.

Only **write operations** on **config/admin pages** get wrapped in `Signal.Begin`. Read-only pages (dashboard, logs, memory viewer) pass through without Signal instrumentation to avoid noise.

## Files

| Action | File | What |
|--------|------|------|
| Modify | `AgentCoreProcessor/WebUI/Components/Layout/MainLayout.razor` | Add `@implements IHandleEvent` + `@code` block |
| Create | `AgentCoreProcessor/WebUI/Services/WebUISignalSource.cs` | Helper: URL-based write detection + Signal wrapping |

**Important:** All Signal names in the code must be in Chinese. Detail fields use English keys.
**Detail standard:** Full context in all details — no truncation. Full URI, full error messages with stacks, full page name extraction. Only exclude binary data and secrets.

---

## Step 1: Create `WebUISignalSource.cs`

**File:** `AgentCoreProcessor/WebUI/Services/WebUISignalSource.cs`

This helper provides the interception logic. Putting it in a separate C# file keeps `MainLayout.razor` clean.

```csharp
using System;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.WebUI.Services
{
    /// <summary>
    /// Centralized Signal source for WebUI-originated write operations.
    /// Detects whether a URI belongs to a config/admin page that performs
    /// state-changing operations requiring Signal instrumentation.
    /// </summary>
    internal static class WebUISignalSource
    {
        /// <summary>Signal scope for all WebUI operations.</summary>
        public const string Scope = "webui:settings";

        /// <summary>
        /// Path prefixes for pages that perform write operations.
        /// Read-only pages (dashboard, logs, memory viewer) are excluded.
        /// </summary>
        private static readonly string[] WritePagePrefixes =
        [
            "/config/",
            "/engine",
            "/adapters",
            "/dream",
            "/tools"   // if added later
        ];

        /// <summary>
        /// Returns true if the given URI belongs to a page that may perform write operations.
        /// </summary>
        public static bool IsWritePage(string uri)
        {
            foreach (var prefix in WritePagePrefixes)
            {
                if (uri.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Creates a Signal.Begin context for a WebUI action and closes it after the callback completes.
        /// The SignalContext is restored to null afterward since WebUI events are external triggers.
        /// </summary>
        public static async Task WrapAsync(string uri, Func<Task> action)
        {
            var page = ExtractPageName(uri);

            using var ctx = Signal.Begin(
                LogGroup.Engine,
                Scope,
                $"WebUI操作: {page}",
                new
                {
                    page,
                    uri,
                    timestamp = DateTime.Now
                });

            try
            {
                await action();
                ctx.Close(new { reason = "completed" });
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "WebUI操作失败", new
                {
                    page,
                    uri,
                    error = ex.GetType().Name,
                    message = ex.Message,
                    stack = ex.StackTrace              // FULL stack
                });
                ctx.Close(new { reason = "failed", error = ex.Message, stack = ex.StackTrace });
                throw;
            }
        }

        /// <summary>
        /// Synchronous wrapper for non-async handlers.
        /// </summary>
        public static void Wrap(string uri, Action action)
        {
            var page = ExtractPageName(uri);

            using var ctx = Signal.Begin(
                LogGroup.Engine,
                Scope,
                $"WebUI操作: {page}",
                new
                {
                    page,
                    uri,
                    timestamp = DateTime.Now
                });

            try
            {
                action();
                ctx.Close(new { reason = "completed" });
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "WebUI操作失败", new
                {
                    page,
                    uri,
                    error = ex.GetType().Name,
                    message = ex.Message,
                    stack = ex.StackTrace              // FULL stack
                });
                ctx.Close(new { reason = "failed", error = ex.Message, stack = ex.StackTrace });
                throw;
            }
        }

        private static string ExtractPageName(string uri)
        {
            // e.g. "/config/tools" → "config/tools"
            // e.g. "/adapters" → "adapters"
            var trimmed = uri.TrimStart('/');
            if (string.IsNullOrEmpty(trimmed)) return "unknown";

            // Strip query string
            var qIdx = trimmed.IndexOf('?');
            if (qIdx >= 0) trimmed = trimmed[..qIdx];

            // Strip trailing slash
            trimmed = trimmed.TrimEnd('/');

            return trimmed;
        }
    }
}
```

## Step 2: Modify `MainLayout.razor`

**File:** `AgentCoreProcessor/WebUI/Components/Layout/MainLayout.razor`

Replace the entire file content:

```razor
@inherits LayoutComponentBase
@implements IHandleEvent
@inject NavigationManager NavManager
@using AgentCoreProcessor.WebUI.Services

<div class="app-layout">
    <NavMenu />
    <main class="app-main">
        @Body
    </main>
</div>

@code {
    Task IHandleEvent.HandleEventAsync(
        Microsoft.AspNetCore.Components.EventCallbackWorkItem callback, object? arg)
    {
        var uri = NavManager.Uri;

        // Only wrap write operations on config/admin pages.
        // Read-only pages (dashboard, logs, memory viewer) pass through directly.
        if (WebUISignalSource.IsWritePage(uri))
        {
            var task = callback.InvokeAsync(arg);
            if (task.Status == TaskStatus.Created)
            {
                // Async handler — wrap in Signal
                return WebUISignalSource.WrapAsync(uri, async () => await task);
            }
            // Sync handler (already completed or faulted)
            return task;
        }

        return callback.InvokeAsync(arg);
    }
}
```

### How It Works

1. **Every** button click, form submit, or UI event from any child component reaches `IHandleEvent.HandleEventAsync`.
2. `NavManager.Uri` gives the current page URL (e.g., `https://localhost:5000/config/tools`).
3. `IsWritePage` checks if the URL starts with a config/admin prefix.
4. If it's a write page: the event handler is wrapped in `Signal.Begin` → handler runs → `ctx.Close`.
5. If it's a read-only page: the event passes through unchanged.
6. The `Signal.Begin` scope is `webui:settings` — all WebUI actions share this scope.
7. Any downstream EventBus/TaskBridge operations that happen during the handler execution will capture the trace IDs automatically.

### What Gets Instrumented

Every write operation on these pages automatically gets a Signal source:

| Page | Example Actions |
|------|----------------|
| `/config/tools` | Enable tool, Disable tool |
| `/config/profiles` | Edit profile |
| `/config/editor` | Save config |
| `/adapters` | Enable, Disable, Remove, Create adapter |
| `/engine` | Toggle mute, Stop engines, Trigger vision |
| `/engine/manage` | Same as above |
| `/dream` | Request sleep, Change config |
| `/adapters/{type}?id={id}` | Adapter-specific settings |

### What Gets Excluded

| Page | Reason |
|------|--------|
| `/` (dashboard) | Read-only |
| `/logs/*` | Read-only |
| `/memory/*` | Read-only |
| `/people` | Read-only |
| `/workers/*` | Read-only |
| `/messages` | Read-only |
| `/images` | Read-only |
| `/console` | Read-only log viewer |
| `/login` | Uses LoginLayout, not MainLayout |

---

## Naming Conventions

| Event | Name | Group |
|-------|------|-------|
| WebUI action | `WebUI操作: {page}` | Engine |
| WebUI action failed | `WebUI操作失败` (error) | Engine |

Signal scope: `webui:settings`

---

## Considerations

### Read vs. Write Detection

The current approach uses URL prefix matching to identify write-capable pages. This means ALL events on those pages get wrapped — including simple data refreshes and UI toggles. This is acceptable because:

1. Signal overhead is negligible (a few JSON writes to SQLite).
2. It's simpler and more robust than trying to detect which specific handler is a "write" — many handlers do mixed read+write.
3. The trace graph naturally shows "when did an admin interact with the config page" which is useful context.

If noise becomes an issue later, the filter can be refined by checking the event callback method name, but that requires reflection and is fragile.

### AsyncLocal Propagation

`Signal.Begin` sets `SignalContext.Current` via AsyncLocal. Because Blazor Server event handlers run on the same SynchronizationContext, the AsyncLocal value propagates through all synchronous and async calls within the handler. This means `AdapterManager.EnableAsync`, `ToolRegistry.DisableTool`, etc. all see the SignalContext.

### SignalContext Restoration

After the WebUI handler completes, `ctx.Close()` is called and the `using` block disposes the context. Since WebUI events are external triggers (not continuations of any prior Signal), the parent context should be null at this point. No explicit `SignalContext.Restore` is needed because there's no parent lifecycle signal to restore to.

### Error Propagation

Exceptions are caught, logged via `Signal.Error`, and re-thrown. Blazor's default error handling (error boundary) will display the error to the user. The `Signal.Error` event will appear as a red dot on the trace page at the point of failure.

---

## Checklist

- [ ] `WebUISignalSource.cs` created with `IsWritePage`, `WrapAsync`, `Wrap` methods
- [ ] `MainLayout.razor` implements `IHandleEvent`
- [ ] `HandleEventAsync` checks `IsWritePage` before wrapping
- [ ] Async handlers wrapped via `WrapAsync`, sync handlers pass through
- [ ] Signal scope is `webui:settings`
- [ ] Signal name format: `WebUI操作: {page}`
- [ ] Errors caught and re-thrown with `Signal.Error`
- [ ] Read-only pages (dashboard, logs, memory, people) excluded
- [ ] All names are in Chinese
- [ ] Build succeeds
