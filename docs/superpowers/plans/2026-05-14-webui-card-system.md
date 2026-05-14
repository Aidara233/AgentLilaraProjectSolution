# WebUI 卡片式数据驱动系统 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Shell infrastructure and data layer for a card-based, data-driven WebUI system where pages are composed of typed cards bound to data sources, and providers declare page structure.

**Architecture:** SDK defines interfaces (IWebUIProvider, IDataSource, CardSchema types). Shell (in AgentCoreProcessor/WebUI/) provides ProviderRegistry, dynamic routing, grid layout, and 7 card renderer Blazor components. PluginLoader is extended to discover IWebUIProvider implementations alongside existing Component/ITool scanning.

**Tech Stack:** .NET 8, Blazor Server (Interactive), System.Text.Json (JsonNode), CSS Grid, SignalR (existing Blazor circuit)

---

## File Structure

### SDK Types (AgentLilara.PluginSDK/)

| File | Responsibility |
|------|---------------|
| `WebUI/IWebUIProvider.cs` | Provider interface + WebUIProviderAttribute |
| `WebUI/PageDefinition.cs` | PageDefinition + PageMeta |
| `WebUI/CardDefinition.cs` | CardDefinition + CardType enum + CardLayout |
| `WebUI/CardSchema.cs` | Abstract CardSchema base + all 7 schema subclasses |
| `WebUI/DataSource.cs` | IDataSource + DataSourceDefinition + DataQuery + DataFilter + DataResult + ActionResult |
| `WebUI/IPageContext.cs` | IPageContext interface |

### Shell Infrastructure (AgentCoreProcessor/WebUI/)

| File | Responsibility |
|------|---------------|
| `Shell/ProviderRegistry.cs` | Register/unregister/query providers, build nav tree |
| `Shell/PageContext.cs` | IPageContext implementation (events, state, navigation) |
| `Shell/DataSourceManager.cs` | Per-page DataSource lifecycle, fetch with retry, subscribe |
| `Components/Shell/DynamicPage.razor` | Route-matched page renderer (grid + card dispatch) |
| `Components/Shell/CardHost.razor` | Single card wrapper (error boundary, loading state, lifecycle) |
| `Components/Shell/CardGrid.razor` | CSS Grid layout container |
| `Components/Cards/TableCard.razor` | Table renderer |
| `Components/Cards/StatusCard.razor` | Status/KV renderer |
| `Components/Cards/FormCard.razor` | Form renderer |
| `Components/Cards/StreamCard.razor` | Log stream renderer |
| `Components/Cards/ChatCard.razor` | Chat renderer |
| `Components/Cards/TreeCard.razor` | Tree renderer |
| `Components/Cards/DetailCard.razor` | Detail renderer |

### Modified Files

| File | Change |
|------|--------|
| `Tool/Host/PluginLoader.cs` | Add IWebUIProvider discovery + registration |
| `WebUI/Navigation/NavConfig.cs` | Replace static list with ProviderRegistry-driven generation |
| `WebUI/Components/Layout/NavMenu.razor` | Read from ProviderRegistry instead of NavConfig.Items |
| `WebUI/Components/Routes.razor` | Add catch-all route for dynamic pages |
| `Program.cs` | Register ProviderRegistry as singleton service |

---

---

### Task 1: SDK — Core Types (IWebUIProvider, PageDefinition, CardDefinition)

**Files:**
- Create: `AgentLilara.PluginSDK/WebUI/IWebUIProvider.cs`
- Create: `AgentLilara.PluginSDK/WebUI/PageDefinition.cs`
- Create: `AgentLilara.PluginSDK/WebUI/CardDefinition.cs`

- [ ] **Step 1: Create IWebUIProvider interface and attribute**

```csharp
// AgentLilara.PluginSDK/WebUI/IWebUIProvider.cs
using System.Collections.Generic;

namespace AgentLilara.PluginSDK.WebUI;

/// <summary>WebUI 页面提供者。插件实现此接口声明页面。</summary>
public interface IWebUIProvider
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<PageDefinition> Pages { get; }
}

[AttributeUsage(AttributeTargets.Class)]
public class WebUIProviderAttribute : Attribute
{
    /// <summary>true = 内置，不可卸载</summary>
    public bool BuiltIn { get; set; }
}
```

- [ ] **Step 2: Create PageDefinition and PageMeta**

```csharp
// AgentLilara.PluginSDK/WebUI/PageDefinition.cs
using System.Collections.Generic;

namespace AgentLilara.PluginSDK.WebUI;

public class PageDefinition
{
    /// <summary>页面唯一标识，用于 URL 路由（如 "dream/status"）</summary>
    public required string Route { get; init; }
    public required PageMeta Meta { get; init; }
    public required IReadOnlyList<CardDefinition> Cards { get; init; }
    public required IReadOnlyList<DataSourceDefinition> DataSources { get; init; }
}

public class PageMeta
{
    public required string Title { get; init; }
    public string? Icon { get; init; }
    public string? Group { get; init; }
    public int Order { get; init; }
    public bool DefaultCollapsed { get; init; }
}
```

- [ ] **Step 3: Create CardDefinition, CardType, and CardLayout**

```csharp
// AgentLilara.PluginSDK/WebUI/CardDefinition.cs
namespace AgentLilara.PluginSDK.WebUI;

public class CardDefinition
{
    public required string Id { get; init; }
    public required CardType Type { get; init; }
    public required string DataSourceId { get; init; }
    public required CardSchema Schema { get; init; }
    public CardLayout Layout { get; init; } = new();
    public string? Title { get; init; }
}

public enum CardType
{
    Table, Status, Form, Stream, Chat, Tree, Detail, Custom
}

public class CardLayout
{
    public string? MinWidth { get; init; }
    public int PreferredCols { get; init; } = 12;
    public string? Height { get; init; }
    public int Order { get; init; }
}
```

- [ ] **Step 4: Verify compilation**

Run: `dotnet build AgentLilara.PluginSDK/AgentLilara.PluginSDK.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 5: Commit**

```bash
git add AgentLilara.PluginSDK/WebUI/
git commit -m "feat(sdk): add WebUI provider core types (IWebUIProvider, PageDefinition, CardDefinition)"
```

---

### Task 2: SDK — CardSchema Types

**Files:**
- Create: `AgentLilara.PluginSDK/WebUI/CardSchema.cs`

- [ ] **Step 1: Create CardSchema base and Table/Status/Form schemas**

```csharp
// AgentLilara.PluginSDK/WebUI/CardSchema.cs
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgentLilara.PluginSDK.WebUI;

[JsonDerivedType(typeof(TableSchema), "table")]
[JsonDerivedType(typeof(StatusSchema), "status")]
[JsonDerivedType(typeof(FormSchema), "form")]
[JsonDerivedType(typeof(StreamSchema), "stream")]
[JsonDerivedType(typeof(ChatSchema), "chat")]
[JsonDerivedType(typeof(TreeSchema), "tree")]
[JsonDerivedType(typeof(DetailSchema), "detail")]
public abstract class CardSchema { }

// --- Table ---

public class TableSchema : CardSchema
{
    public required List<ColumnDef> Columns { get; init; }
    public bool Searchable { get; init; } = true;
    public bool Paginated { get; init; } = true;
    public int DefaultPageSize { get; init; } = 20;
    public List<RowAction>? RowActions { get; init; }
}

public class ColumnDef
{
    public required string Field { get; init; }
    public required string Header { get; init; }
    public bool Sortable { get; init; } = true;
    public string? Width { get; init; }
    public ColumnFormat Format { get; init; } = ColumnFormat.Text;
}

public enum ColumnFormat { Text, DateTime, Badge, Link, Image, Custom }

public class RowAction
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Icon { get; init; }
    public string? Confirm { get; init; }
    public bool Danger { get; init; }
}

// --- Status ---

public class StatusSchema : CardSchema
{
    public required List<StatusField> Fields { get; init; }
    public List<ActionButton>? Actions { get; init; }
}

public class StatusField
{
    public required string Field { get; init; }
    public required string Label { get; init; }
    public StatusFieldType Type { get; init; } = StatusFieldType.Text;
}

public enum StatusFieldType { Text, Badge, Progress, Indicator, DateTime }

public class ActionButton
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Icon { get; init; }
    public string? Confirm { get; init; }
    public bool Danger { get; init; }
}

// --- Form ---

public class FormSchema : CardSchema
{
    public required List<FormField> Fields { get; init; }
    public List<FormGroup>? Groups { get; init; }
    public bool ShowReset { get; init; } = true;
}

public class FormField
{
    public required string Field { get; init; }
    public required string Label { get; init; }
    public FormFieldType Type { get; init; } = FormFieldType.Text;
    public string? Placeholder { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    public List<SelectOption>? Options { get; init; }
    public string? Group { get; init; }
}

public enum FormFieldType { Text, Number, TextArea, Select, Toggle, Radio, Password, Json }

public class FormGroup
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool DefaultCollapsed { get; init; }
}

public class SelectOption
{
    public required string Value { get; init; }
    public required string Label { get; init; }
}

// --- Stream ---

public class StreamSchema : CardSchema
{
    public int MaxLines { get; init; } = 500;
    public bool AutoScroll { get; init; } = true;
    public bool ShowPauseButton { get; init; } = true;
    public bool ShowFilter { get; init; } = true;
}

// --- Chat ---

public class ChatSchema : CardSchema
{
    public bool ShowSenderSwitch { get; init; } = true;
    public bool ShowInput { get; init; } = true;
    public List<string>? Senders { get; init; }
}

// --- Tree ---

public class TreeSchema : CardSchema
{
    public required string NodeIdField { get; init; }
    public required string NodeLabelField { get; init; }
    public string? ParentIdField { get; init; }
    public string? ChildrenField { get; init; }
    public bool Expandable { get; init; } = true;
}

// --- Detail ---

public class DetailSchema : CardSchema
{
    public required List<DetailSection> Sections { get; init; }
}

public class DetailSection
{
    public required string Title { get; init; }
    public required List<DetailField> Fields { get; init; }
    public bool DefaultCollapsed { get; init; }
}

public class DetailField
{
    public required string Field { get; init; }
    public required string Label { get; init; }
    public ColumnFormat Format { get; init; } = ColumnFormat.Text;
    public bool Editable { get; init; }
}
```

- [ ] **Step 2: Verify compilation**

Run: `dotnet build AgentLilara.PluginSDK/AgentLilara.PluginSDK.csproj`
Expected: Build succeeded. 0 Error(s)

Note: The SDK project needs `System.Text.Json` for `JsonDerivedType`. Since it targets .NET 8, `System.Text.Json` is available in-box — no package reference needed.

- [ ] **Step 3: Commit**

```bash
git add AgentLilara.PluginSDK/WebUI/CardSchema.cs
git commit -m "feat(sdk): add CardSchema types (Table, Status, Form, Stream, Chat, Tree, Detail)"
```

---

### Task 3: SDK — DataSource and PageContext Interfaces

**Files:**
- Create: `AgentLilara.PluginSDK/WebUI/DataSource.cs`
- Create: `AgentLilara.PluginSDK/WebUI/IPageContext.cs`

- [ ] **Step 1: Create DataSource types**

```csharp
// AgentLilara.PluginSDK/WebUI/DataSource.cs
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.WebUI;

public class DataSourceDefinition
{
    public required string Id { get; init; }
    public required IDataSource Source { get; init; }
}

public interface IDataSource
{
    Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default);
    Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default);
    bool SupportsPush { get; }
    IDisposable? Subscribe(Action<JsonNode?> callback);
}

public class DataQuery
{
    public int? Page { get; init; }
    public int? PageSize { get; init; }
    public string? Search { get; init; }
    public string? SortBy { get; init; }
    public bool SortDesc { get; init; }
    public List<DataFilter>? Filters { get; init; }
    public JsonNode? Extra { get; init; }
}

public class DataFilter
{
    public required string Field { get; init; }
    public required string Operator { get; init; }
    public required string Value { get; init; }
}

public class DataResult
{
    public required JsonNode Data { get; init; }
    public int? TotalCount { get; init; }
    public JsonNode? Meta { get; init; }
}

public class ActionResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public JsonNode? Data { get; init; }
}
```

- [ ] **Step 2: Create IPageContext interface**

```csharp
// AgentLilara.PluginSDK/WebUI/IPageContext.cs
using System;
using System.Text.Json.Nodes;

namespace AgentLilara.PluginSDK.WebUI;

public interface IPageContext
{
    void Emit(string eventName, JsonNode? payload = null);
    IDisposable On(string eventName, Action<JsonNode?> handler);
    JsonNode? GetState(string key);
    void SetState(string key, JsonNode? value);
    void Navigate(string route);
}
```

- [ ] **Step 3: Verify compilation**

Run: `dotnet build AgentLilara.PluginSDK/AgentLilara.PluginSDK.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 4: Commit**

```bash
git add AgentLilara.PluginSDK/WebUI/DataSource.cs AgentLilara.PluginSDK/WebUI/IPageContext.cs
git commit -m "feat(sdk): add IDataSource, DataQuery, IPageContext interfaces"
```

---

### Task 4: Shell — ProviderRegistry

**Files:**
- Create: `AgentCoreProcessor/WebUI/Shell/ProviderRegistry.cs`

- [ ] **Step 1: Implement ProviderRegistry**

```csharp
// AgentCoreProcessor/WebUI/Shell/ProviderRegistry.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Shell;

/// <summary>
/// 全局 Provider 注册表。管理所有已注册的 IWebUIProvider 及其页面。
/// 线程安全，支持运行时注册/反注册（热重载）。
/// </summary>
internal class ProviderRegistry
{
    private readonly ConcurrentDictionary<string, ProviderEntry> _providers = new();

    public event Action? OnChanged;

    public bool Register(IWebUIProvider provider, bool builtIn = false)
    {
        var entry = new ProviderEntry(provider, builtIn);
        if (!_providers.TryAdd(provider.Id, entry))
            return false;

        FrameworkLogger.Log("ProviderRegistry", $"已注册 Provider: {provider.Id} ({provider.Pages.Count} 页面)");
        OnChanged?.Invoke();
        return true;
    }

    public bool Unregister(string providerId)
    {
        if (!_providers.TryRemove(providerId, out var entry))
            return false;

        if (entry.BuiltIn)
        {
            // 内置不可卸载，放回去
            _providers.TryAdd(providerId, entry);
            return false;
        }

        FrameworkLogger.Log("ProviderRegistry", $"已反注册 Provider: {providerId}");
        OnChanged?.Invoke();
        return true;
    }

    public PageDefinition? FindPage(string route)
    {
        foreach (var entry in _providers.Values)
        {
            foreach (var page in entry.Provider.Pages)
            {
                if (string.Equals(page.Route, route, StringComparison.OrdinalIgnoreCase))
                    return page;
            }
        }
        return null;
    }

    public List<NavGroup> BuildNavTree()
    {
        var groups = new Dictionary<string, NavGroup>();
        var topLevel = new List<PageDefinition>();

        foreach (var entry in _providers.Values)
        {
            foreach (var page in entry.Provider.Pages)
            {
                var group = page.Meta.Group;
                if (string.IsNullOrEmpty(group))
                {
                    topLevel.Add(page);
                }
                else
                {
                    if (!groups.TryGetValue(group, out var g))
                    {
                        g = new NavGroup { Name = group };
                        groups[group] = g;
                    }
                    g.Pages.Add(page);
                    if (page.Meta.DefaultCollapsed)
                        g.DefaultCollapsed = true;
                    if (page.Meta.Icon != null && g.Icon == null)
                        g.Icon = page.Meta.Icon;
                }
            }
        }

        // 排序
        foreach (var g in groups.Values)
            g.Pages.Sort((a, b) => a.Meta.Order.CompareTo(b.Meta.Order));

        var result = new List<NavGroup>();
        // 顶层页面作为单页面组
        foreach (var p in topLevel.OrderBy(p => p.Meta.Order))
            result.Add(new NavGroup { Name = p.Meta.Title, Icon = p.Meta.Icon, Pages = { p }, IsSinglePage = true });

        // 分组
        foreach (var g in groups.Values.OrderBy(g => g.Pages.FirstOrDefault()?.Meta.Order ?? 999))
            result.Add(g);

        return result;
    }

    public IReadOnlyList<ProviderEntry> GetAll() => _providers.Values.ToList();
}

internal record ProviderEntry(IWebUIProvider Provider, bool BuiltIn);

internal class NavGroup
{
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public bool DefaultCollapsed { get; set; }
    public bool IsSinglePage { get; set; }
    public List<PageDefinition> Pages { get; set; } = new();
}
```

- [ ] **Step 2: Verify compilation**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/WebUI/Shell/ProviderRegistry.cs
git commit -m "feat(webui): add ProviderRegistry for provider management and nav tree building"
```

---

### Task 5: Shell — PageContext Implementation

**Files:**
- Create: `AgentCoreProcessor/WebUI/Shell/PageContext.cs`

- [ ] **Step 1: Implement PageContext**

```csharp
// AgentCoreProcessor/WebUI/Shell/PageContext.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK.WebUI;
using Microsoft.AspNetCore.Components;

namespace AgentCoreProcessor.WebUI.Shell;

internal class PageContext : IPageContext, IDisposable
{
    private readonly NavigationManager _nav;
    private readonly ConcurrentDictionary<string, JsonNode?> _state = new();
    private readonly ConcurrentDictionary<string, List<Action<JsonNode?>>> _handlers = new();

    public PageContext(NavigationManager nav)
    {
        _nav = nav;
    }

    public void Emit(string eventName, JsonNode? payload = null)
    {
        if (_handlers.TryGetValue(eventName, out var list))
        {
            foreach (var handler in list.ToArray())
            {
                try { handler(payload); }
                catch { /* 卡片事件处理不应影响其他卡片 */ }
            }
        }
    }

    public IDisposable On(string eventName, Action<JsonNode?> handler)
    {
        var list = _handlers.GetOrAdd(eventName, _ => new List<Action<JsonNode?>>());
        lock (list) { list.Add(handler); }
        return new Subscription(() =>
        {
            lock (list) { list.Remove(handler); }
        });
    }

    public JsonNode? GetState(string key)
        => _state.TryGetValue(key, out var val) ? val : null;

    public void SetState(string key, JsonNode? value)
        => _state[key] = value;

    public void Navigate(string route)
        => _nav.NavigateTo("/" + route.TrimStart('/'));

    public void Dispose()
    {
        _handlers.Clear();
        _state.Clear();
    }

    private class Subscription : IDisposable
    {
        private readonly Action _onDispose;
        public Subscription(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}
```

- [ ] **Step 2: Verify compilation**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/WebUI/Shell/PageContext.cs
git commit -m "feat(webui): add PageContext implementation (events, state, navigation)"
```

---

### Task 6: Shell — DataSourceManager (Fetch with Retry + Subscribe)

**Files:**
- Create: `AgentCoreProcessor/WebUI/Shell/DataSourceManager.cs`

- [ ] **Step 1: Implement DataSourceManager**

```csharp
// AgentCoreProcessor/WebUI/Shell/DataSourceManager.cs
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Shell;

/// <summary>
/// 管理一个页面内所有 DataSource 的生命周期。
/// 提供带重试的 Fetch、Subscribe 管理、错误状态跟踪。
/// </summary>
internal class DataSourceManager : IDisposable
{
    private readonly Dictionary<string, IDataSource> _sources = new();
    private readonly Dictionary<string, IDisposable?> _subscriptions = new();
    private readonly Dictionary<string, DataSourceState> _states = new();

    public event Action<string>? OnDataChanged; // dataSourceId

    public void Initialize(IReadOnlyList<DataSourceDefinition> definitions)
    {
        foreach (var def in definitions)
        {
            _sources[def.Id] = def.Source;
            _states[def.Id] = new DataSourceState();
        }
    }

    public async Task<DataResult?> FetchAsync(string dataSourceId, DataQuery? query = null, CancellationToken ct = default)
    {
        if (!_sources.TryGetValue(dataSourceId, out var source))
            return null;

        var state = _states[dataSourceId];
        state.IsLoading = true;
        state.Error = null;

        for (int attempt = 0; attempt <= 3; attempt++)
        {
            try
            {
                var result = await source.FetchAsync(query, ct);
                state.IsLoading = false;
                state.LastData = result;
                state.RetryCount = 0;
                return result;
            }
            catch (Exception ex) when (attempt < 3)
            {
                state.RetryCount = attempt + 1;
                var delay = (int)Math.Pow(2, attempt) * 1000; // 1s, 2s, 4s
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                state.IsLoading = false;
                state.Error = ex.Message;
                return null;
            }
        }

        return null;
    }

    public async Task<ActionResult> SubmitAsync(string dataSourceId, string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (!_sources.TryGetValue(dataSourceId, out var source))
            return new ActionResult { Success = false, Message = "数据源不存在" };

        try
        {
            return await source.SubmitAsync(action, data, ct);
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Message = ex.Message };
        }
    }

    public void SubscribeAll()
    {
        foreach (var (id, source) in _sources)
        {
            if (!source.SupportsPush) continue;
            _subscriptions[id] = source.Subscribe(payload =>
            {
                OnDataChanged?.Invoke(id);
            });
        }
    }

    public DataSourceState? GetState(string dataSourceId)
        => _states.TryGetValue(dataSourceId, out var s) ? s : null;

    public void Dispose()
    {
        foreach (var sub in _subscriptions.Values)
            sub?.Dispose();
        _subscriptions.Clear();
        _sources.Clear();
        _states.Clear();
    }
}

internal class DataSourceState
{
    public bool IsLoading { get; set; }
    public string? Error { get; set; }
    public DataResult? LastData { get; set; }
    public int RetryCount { get; set; }
}
```

- [ ] **Step 2: Verify compilation**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/WebUI/Shell/DataSourceManager.cs
git commit -m "feat(webui): add DataSourceManager with retry logic and push subscription"
```

---

### Task 7: Shell — CardGrid Layout Component

**Files:**
- Create: `AgentCoreProcessor/WebUI/Components/Shell/CardGrid.razor`

- [ ] **Step 1: Create CardGrid component**

```razor
@* AgentCoreProcessor/WebUI/Components/Shell/CardGrid.razor *@

<div class="card-grid" style="@GridStyle">
    @ChildContent
</div>

@code {
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private string GridStyle => "display:grid; grid-template-columns:repeat(12, 1fr); gap:1rem; padding:1rem;";
}
```

- [ ] **Step 2: Add card-grid CSS to app.css**

Append to `AgentCoreProcessor/WebUI/wwwroot/css/app.css`:

```css
/* === Card Grid System === */
.card-grid {
    width: 100%;
}

.card-grid-item {
    min-width: 0; /* prevent grid blowout */
}

@media (max-width: 768px) {
    .card-grid {
        grid-template-columns: 1fr !important;
    }
    .card-grid-item {
        grid-column: span 12 !important;
    }
}
```

- [ ] **Step 3: Verify compilation**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/WebUI/Components/Shell/CardGrid.razor AgentCoreProcessor/WebUI/wwwroot/css/app.css
git commit -m "feat(webui): add CardGrid responsive layout component"
```

---

### Task 8: Shell — CardHost Component (Error Boundary + Loading)

**Files:**
- Create: `AgentCoreProcessor/WebUI/Components/Shell/CardHost.razor`

- [ ] **Step 1: Create CardHost component**

```razor
@* AgentCoreProcessor/WebUI/Components/Shell/CardHost.razor *@
@using AgentLilara.PluginSDK.WebUI
@using AgentCoreProcessor.WebUI.Shell

<div class="card card-grid-item" style="grid-column: span @Layout.PreferredCols; @HeightStyle @MinWidthStyle">
    @if (!string.IsNullOrEmpty(Title))
    {
        <div class="card-header">
            <span class="card-title">@Title</span>
            <button class="btn btn-sm btn-outline-secondary ms-auto" @onclick="OnRefreshClick" title="刷新">
                <i class="bi bi-arrow-clockwise"></i>
            </button>
        </div>
    }
    <div class="card-body p-0">
        @if (State?.Error != null)
        {
            <div class="card-error-state">
                <i class="bi bi-exclamation-triangle text-danger"></i>
                <span>@State.Error</span>
                <button class="btn btn-sm btn-outline-danger" @onclick="OnRefreshClick">重试</button>
            </div>
        }
        else if (State?.IsLoading == true && State.LastData == null)
        {
            <div class="card-loading-state">
                <div class="spinner-border spinner-border-sm" role="status"></div>
                <span>加载中...</span>
            </div>
        }
        else
        {
            @ChildContent
        }
    </div>
</div>

@code {
    [Parameter] public string? Title { get; set; }
    [Parameter] public CardLayout Layout { get; set; } = new();
    [Parameter] public DataSourceState? State { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public EventCallback OnRefresh { get; set; }

    private string HeightStyle => Layout.Height != null ? $"height:{Layout.Height};" : "";
    private string MinWidthStyle => Layout.MinWidth != null ? $"min-width:{Layout.MinWidth};" : "";

    private async Task OnRefreshClick() => await OnRefresh.InvokeAsync();
}
```

- [ ] **Step 2: Add card host CSS**

Append to `AgentCoreProcessor/WebUI/wwwroot/css/app.css`:

```css
/* === Card Host === */
.card-error-state,
.card-loading-state {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    padding: 2rem;
    color: var(--text-muted);
}

.card-error-state {
    border: 1px solid var(--danger-color, #dc3545);
    border-radius: 0.25rem;
    margin: 0.5rem;
}
```

- [ ] **Step 3: Verify compilation**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/WebUI/Components/Shell/CardHost.razor AgentCoreProcessor/WebUI/wwwroot/css/app.css
git commit -m "feat(webui): add CardHost component with error/loading states"
```

---

### Task 9: Shell — Card Renderers (Table + Status)

**Files:**
- Create: `AgentCoreProcessor/WebUI/Components/Cards/TableCard.razor`
- Create: `AgentCoreProcessor/WebUI/Components/Cards/StatusCard.razor`

- [ ] **Step 1: Create TableCard renderer**

```razor
@* AgentCoreProcessor/WebUI/Components/Cards/TableCard.razor *@
@using AgentLilara.PluginSDK.WebUI
@using System.Text.Json.Nodes

<div class="table-card">
    @if (Schema.Searchable)
    {
        <div class="p-2">
            <input type="text" class="form-control form-control-sm"
                   placeholder="搜索..." @bind="searchText" @bind:event="oninput"
                   @onkeyup="OnSearchKeyUp" />
        </div>
    }
    <div class="table-responsive">
        <table class="table-dark-custom">
            <thead>
                <tr>
                    @foreach (var col in Schema.Columns)
                    {
                        <th style="@(col.Width != null ? $"width:{col.Width}" : "") @(col.Sortable ? "cursor:pointer" : "")"
                            @onclick="() => ToggleSort(col)">
                            @col.Header
                            @if (currentSort == col.Field)
                            {
                                <i class="bi @(sortDesc ? "bi-caret-down-fill" : "bi-caret-up-fill") ms-1"></i>
                            }
                        </th>
                    }
                    @if (Schema.RowActions?.Count > 0)
                    {
                        <th style="width:auto">操作</th>
                    }
                </tr>
            </thead>
            <tbody>
                @if (rows.Count == 0)
                {
                    <tr><td colspan="@ColSpan" class="text-center text-muted-custom" style="padding:1.5rem">无数据</td></tr>
                }
                else
                {
                    @foreach (var row in rows)
                    {
                        <tr>
                            @foreach (var col in Schema.Columns)
                            {
                                <td>@GetCellValue(row, col)</td>
                            }
                            @if (Schema.RowActions?.Count > 0)
                            {
                                <td>
                                    @foreach (var action in Schema.RowActions)
                                    {
                                        <button class="btn btn-sm @(action.Danger ? "btn-outline-danger" : "btn-outline-secondary") me-1"
                                                @onclick="() => OnAction(action, row)">
                                            @if (action.Icon != null) { <i class="bi @action.Icon"></i> }
                                            @action.Label
                                        </button>
                                    }
                                </td>
                            }
                        </tr>
                    }
                }
            </tbody>
        </table>
    </div>
    @if (Schema.Paginated && TotalCount > 0)
    {
        <div class="d-flex justify-content-between align-items-center p-2">
            <span class="text-muted-custom">共 @TotalCount 条</span>
            <div>
                <button class="btn btn-sm btn-outline-secondary" disabled="@(currentPage <= 1)" @onclick="PrevPage">上一页</button>
                <span class="mx-2">@currentPage / @TotalPages</span>
                <button class="btn btn-sm btn-outline-secondary" disabled="@(currentPage >= TotalPages)" @onclick="NextPage">下一页</button>
            </div>
        </div>
    }
</div>

@code {
    [Parameter] public TableSchema Schema { get; set; } = null!;
    [Parameter] public JsonNode? Data { get; set; }
    [Parameter] public int? TotalCount { get; set; }
    [Parameter] public EventCallback<DataQuery> OnQueryChanged { get; set; }
    [Parameter] public EventCallback<(string ActionId, JsonNode Row)> OnRowAction { get; set; }

    private string searchText = "";
    private string? currentSort;
    private bool sortDesc;
    private int currentPage = 1;
    private List<JsonNode> rows = new();

    private int PageSize => Schema.DefaultPageSize;
    private int TotalPages => TotalCount > 0 ? (int)Math.Ceiling((double)TotalCount.Value / PageSize) : 1;
    private int ColSpan => Schema.Columns.Count + (Schema.RowActions?.Count > 0 ? 1 : 0);

    protected override void OnParametersSet()
    {
        rows = Data is JsonArray arr ? arr.Select(n => n!).ToList() : new();
    }

    private string GetCellValue(JsonNode row, ColumnDef col)
    {
        var val = row[col.Field];
        if (val == null) return "";
        return col.Format switch
        {
            ColumnFormat.DateTime => DateTime.TryParse(val.ToString(), out var dt) ? dt.ToString("yyyy-MM-dd HH:mm") : val.ToString(),
            _ => val.ToString()
        };
    }

    private async Task ToggleSort(ColumnDef col)
    {
        if (!col.Sortable) return;
        if (currentSort == col.Field) sortDesc = !sortDesc;
        else { currentSort = col.Field; sortDesc = false; }
        await EmitQuery();
    }

    private async Task OnSearchKeyUp()
    {
        currentPage = 1;
        await EmitQuery();
    }

    private async Task PrevPage() { currentPage--; await EmitQuery(); }
    private async Task NextPage() { currentPage++; await EmitQuery(); }

    private async Task EmitQuery()
    {
        await OnQueryChanged.InvokeAsync(new DataQuery
        {
            Page = currentPage,
            PageSize = PageSize,
            Search = string.IsNullOrWhiteSpace(searchText) ? null : searchText,
            SortBy = currentSort,
            SortDesc = sortDesc
        });
    }

    private async Task OnAction(RowAction action, JsonNode row)
    {
        await OnRowAction.InvokeAsync((action.Id, row));
    }
}
```

- [ ] **Step 2: Create StatusCard renderer**

```razor
@* AgentCoreProcessor/WebUI/Components/Cards/StatusCard.razor *@
@using AgentLilara.PluginSDK.WebUI
@using System.Text.Json.Nodes

<div class="status-card p-3">
    <div class="status-fields">
        @foreach (var field in Schema.Fields)
        {
            <div class="status-field">
                <span class="status-label">@field.Label</span>
                <span class="status-value @GetFieldClass(field)">@GetFieldValue(field)</span>
            </div>
        }
    </div>
    @if (Schema.Actions?.Count > 0)
    {
        <div class="status-actions mt-3">
            @foreach (var action in Schema.Actions)
            {
                <button class="btn btn-sm @(action.Danger ? "btn-outline-danger" : "btn-outline-primary") me-2"
                        @onclick="() => OnActionClick(action)">
                    @if (action.Icon != null) { <i class="bi @action.Icon me-1"></i> }
                    @action.Label
                </button>
            }
        </div>
    }
</div>

@code {
    [Parameter] public StatusSchema Schema { get; set; } = null!;
    [Parameter] public JsonNode? Data { get; set; }
    [Parameter] public EventCallback<string> OnAction { get; set; }

    private string GetFieldValue(StatusField field)
    {
        if (Data == null) return "-";
        var val = Data[field.Field];
        if (val == null) return "-";
        return field.Type switch
        {
            StatusFieldType.DateTime => DateTime.TryParse(val.ToString(), out var dt) ? dt.ToString("yyyy-MM-dd HH:mm:ss") : val.ToString(),
            _ => val.ToString()
        };
    }

    private string GetFieldClass(StatusField field)
    {
        return field.Type switch
        {
            StatusFieldType.Badge => "badge bg-info",
            StatusFieldType.Indicator => GetIndicatorClass(),
            _ => ""
        };

        string GetIndicatorClass()
        {
            var val = Data?[field.Field]?.ToString()?.ToLower();
            return val switch
            {
                "running" or "active" or "online" => "text-success",
                "stopped" or "offline" => "text-danger",
                "idle" or "sleeping" => "text-warning",
                _ => ""
            };
        }
    }

    private async Task OnActionClick(ActionButton action)
    {
        await OnAction.InvokeAsync(action.Id);
    }
}
```

- [ ] **Step 3: Verify compilation**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/WebUI/Components/Cards/TableCard.razor AgentCoreProcessor/WebUI/Components/Cards/StatusCard.razor
git commit -m "feat(webui): add TableCard and StatusCard renderers"
```

---

### Task 10: Shell — Card Renderers (Form + Stream)

**Files:**
- Create: `AgentCoreProcessor/WebUI/Components/Cards/FormCard.razor`
- Create: `AgentCoreProcessor/WebUI/Components/Cards/StreamCard.razor`

- [ ] **Step 1: Create FormCard renderer**

```razor
@* AgentCoreProcessor/WebUI/Components/Cards/FormCard.razor *@
@using AgentLilara.PluginSDK.WebUI
@using System.Text.Json.Nodes

<div class="form-card p-3">
    @if (Schema.Groups?.Count > 0)
    {
        @foreach (var group in Schema.Groups)
        {
            <fieldset class="mb-3">
                <legend class="fs-6 fw-bold" @onclick="() => ToggleGroup(group.Name)" style="cursor:pointer">
                    <i class="bi @(IsGroupCollapsed(group.Name) ? "bi-chevron-right" : "bi-chevron-down") me-1"></i>
                    @group.Name
                </legend>
                @if (!IsGroupCollapsed(group.Name))
                {
                    @foreach (var field in Schema.Fields.Where(f => f.Group == group.Name))
                    {
                        @RenderField(field)
                    }
                }
            </fieldset>
        }
        @* 无分组的字段 *@
        @foreach (var field in Schema.Fields.Where(f => string.IsNullOrEmpty(f.Group)))
        {
            @RenderField(field)
        }
    }
    else
    {
        @foreach (var field in Schema.Fields)
        {
            @RenderField(field)
        }
    }
    <div class="mt-3 d-flex gap-2">
        <button class="btn btn-sm btn-primary" @onclick="OnSave">保存</button>
        @if (Schema.ShowReset)
        {
            <button class="btn btn-sm btn-outline-secondary" @onclick="OnReset">重置</button>
        }
    </div>
</div>

@code {
    [Parameter] public FormSchema Schema { get; set; } = null!;
    [Parameter] public JsonNode? Data { get; set; }
    [Parameter] public EventCallback<JsonNode> OnSubmit { get; set; }

    private Dictionary<string, string> formValues = new();
    private HashSet<string> collapsedGroups = new();

    protected override void OnParametersSet()
    {
        if (Data == null) return;
        foreach (var field in Schema.Fields)
        {
            var val = Data[field.Field]?.ToString() ?? "";
            formValues[field.Field] = val;
        }
        // 初始化折叠状态
        if (Schema.Groups != null)
        {
            foreach (var g in Schema.Groups.Where(g => g.DefaultCollapsed))
                collapsedGroups.Add(g.Name);
        }
    }

    private RenderFragment RenderField(FormField field) => __builder =>
    {
        <div class="mb-2">
            <label class="form-label form-label-sm">@field.Label @(field.Required ? "*" : "")</label>
            @switch (field.Type)
            {
                case FormFieldType.TextArea:
                    <textarea class="form-control form-control-sm" rows="3"
                              placeholder="@field.Placeholder"
                              @bind="formValues[field.Field]"></textarea>
                    break;
                case FormFieldType.Select:
                    <select class="form-select form-select-sm" @bind="formValues[field.Field]">
                        @if (field.Options != null)
                        {
                            @foreach (var opt in field.Options)
                            {
                                <option value="@opt.Value">@opt.Label</option>
                            }
                        }
                    </select>
                    break;
                case FormFieldType.Toggle:
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox"
                               checked="@(formValues.GetValueOrDefault(field.Field) == "true")"
                               @onchange="e => formValues[field.Field] = e.Value?.ToString() ?? "false"" />
                    </div>
                    break;
                case FormFieldType.Number:
                    <input type="number" class="form-control form-control-sm"
                           placeholder="@field.Placeholder"
                           @bind="formValues[field.Field]" />
                    break;
                default:
                    <input type="@(field.Type == FormFieldType.Password ? "password" : "text")"
                           class="form-control form-control-sm"
                           placeholder="@field.Placeholder"
                           @bind="formValues[field.Field]" />
                    break;
            }
            @if (!string.IsNullOrEmpty(field.Description))
            {
                <small class="text-muted-custom">@field.Description</small>
            }
        </div>
    };

    private bool IsGroupCollapsed(string name) => collapsedGroups.Contains(name);
    private void ToggleGroup(string name)
    {
        if (!collapsedGroups.Remove(name)) collapsedGroups.Add(name);
    }

    private async Task OnSave()
    {
        var obj = new JsonObject();
        foreach (var (key, val) in formValues)
            obj[key] = val;
        await OnSubmit.InvokeAsync(obj);
    }

    private void OnReset() => OnParametersSet();
}
```

- [ ] **Step 2: Create StreamCard renderer**

```razor
@* AgentCoreProcessor/WebUI/Components/Cards/StreamCard.razor *@
@using AgentLilara.PluginSDK.WebUI
@using System.Text.Json.Nodes
@implements IDisposable

<div class="stream-card" style="height:100%; display:flex; flex-direction:column;">
    @if (Schema.ShowFilter)
    {
        <div class="p-2 d-flex gap-2">
            <input type="text" class="form-control form-control-sm"
                   placeholder="过滤..." @bind="filterText" @bind:event="oninput" />
            @if (Schema.ShowPauseButton)
            {
                <button class="btn btn-sm @(paused ? "btn-warning" : "btn-outline-secondary")"
                        @onclick="TogglePause">
                    <i class="bi @(paused ? "bi-play-fill" : "bi-pause-fill")"></i>
                </button>
            }
        </div>
    }
    <div class="stream-content flex-grow-1" style="overflow-y:auto; font-family:monospace; font-size:0.8rem; padding:0.5rem;">
        @foreach (var line in FilteredLines)
        {
            <div class="stream-line">@line</div>
        }
    </div>
</div>

@code {
    [Parameter] public StreamSchema Schema { get; set; } = null!;
    [Parameter] public JsonNode? Data { get; set; }
    [Parameter] public EventCallback<JsonNode?> OnPushReceived { get; set; }

    private List<string> lines = new();
    private string filterText = "";
    private bool paused;

    private IEnumerable<string> FilteredLines =>
        string.IsNullOrWhiteSpace(filterText)
            ? lines
            : lines.Where(l => l.Contains(filterText, StringComparison.OrdinalIgnoreCase));

    protected override void OnParametersSet()
    {
        if (Data is JsonArray arr)
        {
            lines = arr.Select(n => n?.ToString() ?? "").ToList();
            TrimLines();
        }
    }

    public void AppendLine(string line)
    {
        if (paused) return;
        lines.Add(line);
        TrimLines();
    }

    private void TrimLines()
    {
        while (lines.Count > Schema.MaxLines)
            lines.RemoveAt(0);
    }

    private void TogglePause() => paused = !paused;

    public void Dispose() { }
}
```

- [ ] **Step 3: Verify compilation**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/WebUI/Components/Cards/FormCard.razor AgentCoreProcessor/WebUI/Components/Cards/StreamCard.razor
git commit -m "feat(webui): add FormCard and StreamCard renderers"
```

---

### Task 11: Shell — Card Renderers (Chat + Tree + Detail)

**Files:**
- Create: `AgentCoreProcessor/WebUI/Components/Cards/ChatCard.razor`
- Create: `AgentCoreProcessor/WebUI/Components/Cards/TreeCard.razor`
- Create: `AgentCoreProcessor/WebUI/Components/Cards/DetailCard.razor`

- [ ] **Step 1: Create ChatCard renderer**

```razor
@* AgentCoreProcessor/WebUI/Components/Cards/ChatCard.razor *@
@using AgentLilara.PluginSDK.WebUI
@using System.Text.Json.Nodes

<div class="chat-card" style="height:100%; display:flex; flex-direction:column;">
    <div class="chat-messages flex-grow-1" style="overflow-y:auto; padding:0.75rem;">
        @foreach (var msg in messages)
        {
            <div class="chat-message mb-2 @(msg.IsBot ? "chat-bot" : "chat-user")">
                <div class="chat-sender text-muted-custom" style="font-size:0.75rem;">@msg.Sender</div>
                <div class="chat-bubble">@msg.Text</div>
            </div>
        }
    </div>
    @if (Schema.ShowInput)
    {
        <div class="chat-input p-2 d-flex gap-2">
            @if (Schema.ShowSenderSwitch && Schema.Senders?.Count > 1)
            {
                <select class="form-select form-select-sm" style="width:auto" @bind="currentSender">
                    @foreach (var s in Schema.Senders)
                    {
                        <option value="@s">@s</option>
                    }
                </select>
            }
            <input type="text" class="form-control form-control-sm" placeholder="输入消息..."
                   @bind="inputText" @onkeyup="OnKeyUp" />
            <button class="btn btn-sm btn-primary" @onclick="Send">发送</button>
        </div>
    }
</div>

@code {
    [Parameter] public ChatSchema Schema { get; set; } = null!;
    [Parameter] public JsonNode? Data { get; set; }
    [Parameter] public EventCallback<JsonNode> OnSubmit { get; set; }

    private List<ChatMessage> messages = new();
    private string inputText = "";
    private string currentSender = "";

    protected override void OnParametersSet()
    {
        currentSender = Schema.Senders?.FirstOrDefault() ?? "user";
        if (Data is JsonArray arr)
        {
            messages = arr.Select(n => new ChatMessage
            {
                Sender = n?["sender"]?.ToString() ?? "",
                Text = n?["text"]?.ToString() ?? "",
                IsBot = n?["isBot"]?.GetValue<bool>() ?? false
            }).ToList();
        }
    }

    private async Task OnKeyUp(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await Send();
    }

    private async Task Send()
    {
        if (string.IsNullOrWhiteSpace(inputText)) return;
        var payload = new JsonObject
        {
            ["sender"] = currentSender,
            ["text"] = inputText
        };
        inputText = "";
        await OnSubmit.InvokeAsync(payload);
    }

    private record ChatMessage { public string Sender { get; init; } = ""; public string Text { get; init; } = ""; public bool IsBot { get; init; } }
}
```

- [ ] **Step 2: Create TreeCard renderer**

```razor
@* AgentCoreProcessor/WebUI/Components/Cards/TreeCard.razor *@
@using AgentLilara.PluginSDK.WebUI
@using System.Text.Json.Nodes

<div class="tree-card p-3">
    @foreach (var node in rootNodes)
    {
        RenderNode(node, 0);
    }
</div>

@code {
    [Parameter] public TreeSchema Schema { get; set; } = null!;
    [Parameter] public JsonNode? Data { get; set; }
    [Parameter] public EventCallback<string> OnNodeSelect { get; set; }

    private List<TreeNode> rootNodes = new();
    private string? selectedId;

    protected override void OnParametersSet()
    {
        if (Data is not JsonArray arr) return;
        var allNodes = arr.Select(n => new TreeNode
        {
            Id = n?[Schema.NodeIdField]?.ToString() ?? "",
            Label = n?[Schema.NodeLabelField]?.ToString() ?? "",
            ParentId = Schema.ParentIdField != null ? n?[Schema.ParentIdField]?.ToString() : null,
            Raw = n!
        }).ToList();

        if (Schema.ParentIdField != null)
        {
            var lookup = allNodes.ToDictionary(n => n.Id);
            foreach (var node in allNodes)
            {
                if (node.ParentId != null && lookup.TryGetValue(node.ParentId, out var parent))
                    parent.Children.Add(node);
            }
            rootNodes = allNodes.Where(n => string.IsNullOrEmpty(n.ParentId) || !lookup.ContainsKey(n.ParentId!)).ToList();
        }
        else
        {
            rootNodes = allNodes;
        }
    }

    private void RenderNode(TreeNode node, int depth)
    {
        <div class="tree-node @(selectedId == node.Id ? "tree-selected" : "")"
             style="padding-left:@(depth * 1.2)rem; cursor:pointer;"
             @onclick="() => SelectNode(node)">
            @if (Schema.Expandable && node.Children.Count > 0)
            {
                <i class="bi @(node.Expanded ? "bi-chevron-down" : "bi-chevron-right") me-1"
                   @onclick:stopPropagation @onclick="() => node.Expanded = !node.Expanded"></i>
            }
            <span>@node.Label</span>
        </div>
        @if (node.Expanded)
        {
            @foreach (var child in node.Children)
            {
                RenderNode(child, depth + 1);
            }
        }
    }

    private async Task SelectNode(TreeNode node)
    {
        selectedId = node.Id;
        await OnNodeSelect.InvokeAsync(node.Id);
    }

    private class TreeNode
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public string? ParentId { get; init; }
        public JsonNode Raw { get; init; } = null!;
        public List<TreeNode> Children { get; set; } = new();
        public bool Expanded { get; set; } = true;
    }
}
```

- [ ] **Step 3: Create DetailCard renderer**

```razor
@* AgentCoreProcessor/WebUI/Components/Cards/DetailCard.razor *@
@using AgentLilara.PluginSDK.WebUI
@using System.Text.Json.Nodes

<div class="detail-card p-3">
    @foreach (var section in Schema.Sections)
    {
        <div class="detail-section mb-3">
            <h6 class="fw-bold" style="cursor:pointer" @onclick="() => ToggleSection(section.Title)">
                <i class="bi @(IsSectionCollapsed(section.Title) ? "bi-chevron-right" : "bi-chevron-down") me-1"></i>
                @section.Title
            </h6>
            @if (!IsSectionCollapsed(section.Title))
            {
                <div class="detail-fields">
                    @foreach (var field in section.Fields)
                    {
                        <div class="detail-field d-flex mb-1">
                            <span class="detail-label text-muted-custom" style="min-width:120px;">@field.Label</span>
                            @if (field.Editable && editing == field.Field)
                            {
                                <input type="text" class="form-control form-control-sm" style="max-width:300px"
                                       @bind="editValue" @onblur="() => SaveEdit(field)" />
                            }
                            else
                            {
                                <span class="detail-value" @ondblclick="() => StartEdit(field)">
                                    @GetValue(field)
                                </span>
                            }
                        </div>
                    }
                </div>
            }
        </div>
    }
</div>

@code {
    [Parameter] public DetailSchema Schema { get; set; } = null!;
    [Parameter] public JsonNode? Data { get; set; }
    [Parameter] public EventCallback<JsonNode> OnSubmit { get; set; }

    private HashSet<string> collapsedSections = new();
    private string? editing;
    private string editValue = "";

    protected override void OnParametersSet()
    {
        foreach (var s in Schema.Sections.Where(s => s.DefaultCollapsed))
            collapsedSections.Add(s.Title);
    }

    private bool IsSectionCollapsed(string title) => collapsedSections.Contains(title);
    private void ToggleSection(string title)
    {
        if (!collapsedSections.Remove(title)) collapsedSections.Add(title);
    }

    private string GetValue(DetailField field)
    {
        var val = Data?[field.Field];
        if (val == null) return "-";
        return field.Format switch
        {
            ColumnFormat.DateTime => DateTime.TryParse(val.ToString(), out var dt) ? dt.ToString("yyyy-MM-dd HH:mm:ss") : val.ToString(),
            _ => val.ToString()
        };
    }

    private void StartEdit(DetailField field)
    {
        if (!field.Editable) return;
        editing = field.Field;
        editValue = Data?[field.Field]?.ToString() ?? "";
    }

    private async Task SaveEdit(DetailField field)
    {
        editing = null;
        await OnSubmit.InvokeAsync(new JsonObject { [field.Field] = editValue });
    }
}
```

- [ ] **Step 4: Verify compilation**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 5: Commit**

```bash
git add AgentCoreProcessor/WebUI/Components/Cards/
git commit -m "feat(webui): add ChatCard, TreeCard, DetailCard renderers"
```

---

### Task 12: Shell — DynamicPage (Route Matching + Card Dispatch)

**Files:**
- Create: `AgentCoreProcessor/WebUI/Components/Shell/DynamicPage.razor`

- [ ] **Step 1: Create DynamicPage component**

```razor
@* AgentCoreProcessor/WebUI/Components/Shell/DynamicPage.razor *@
@page "/p/{*Route}"
@attribute [Microsoft.AspNetCore.Authorization.Authorize]
@using AgentLilara.PluginSDK.WebUI
@using AgentCoreProcessor.WebUI.Shell
@using AgentCoreProcessor.WebUI.Components.Cards
@using System.Text.Json.Nodes
@inject ProviderRegistry Registry
@inject NavigationManager Nav
@implements IDisposable

@if (page == null)
{
    <div class="p-4 text-center text-muted-custom">
        <i class="bi bi-question-circle" style="font-size:2rem"></i>
        <p>页面未找到: @Route</p>
    </div>
}
else
{
    <div class="p-3">
        <h4 class="mb-3">@page.Meta.Title</h4>
        <CardGrid>
            @foreach (var card in page.Cards.OrderBy(c => c.Layout.Order))
            {
                var state = dsManager.GetState(card.DataSourceId);
                <CardHost Title="@card.Title" Layout="@card.Layout" State="@state"
                          OnRefresh="() => RefreshCard(card)">
                    @RenderCard(card, state)
                </CardHost>
            }
        </CardGrid>
    </div>
}

@code {
    [Parameter] public string? Route { get; set; }

    private PageDefinition? page;
    private DataSourceManager dsManager = new();
    private PageContext? pageContext;

    protected override async Task OnParametersSetAsync()
    {
        Cleanup();

        page = Registry.FindPage(Route ?? "");
        if (page == null) return;

        pageContext = new PageContext(Nav);
        dsManager = new DataSourceManager();
        dsManager.Initialize(page.DataSources);
        dsManager.OnDataChanged += OnDataSourceChanged;
        dsManager.SubscribeAll();

        // 首次 Fetch 所有数据源
        foreach (var ds in page.DataSources)
        {
            await dsManager.FetchAsync(ds.Id);
        }
    }

    private async Task RefreshCard(CardDefinition card)
    {
        await dsManager.FetchAsync(card.DataSourceId);
        StateHasChanged();
    }

    private void OnDataSourceChanged(string dataSourceId)
    {
        InvokeAsync(async () =>
        {
            await dsManager.FetchAsync(dataSourceId);
            StateHasChanged();
        });
    }

    private RenderFragment RenderCard(CardDefinition card, DataSourceState? state) => __builder =>
    {
        var data = state?.LastData?.Data;
        var totalCount = state?.LastData?.TotalCount;

        switch (card.Type)
        {
            case CardType.Table:
                <TableCard Schema="@((TableSchema)card.Schema)" Data="@data" TotalCount="@totalCount"
                           OnQueryChanged="q => OnQuery(card.DataSourceId, q)"
                           OnRowAction="e => OnRowAction(card.DataSourceId, e)" />
                break;
            case CardType.Status:
                <StatusCard Schema="@((StatusSchema)card.Schema)" Data="@data"
                            OnAction="id => OnSubmitAction(card.DataSourceId, id, null)" />
                break;
            case CardType.Form:
                <FormCard Schema="@((FormSchema)card.Schema)" Data="@data"
                          OnSubmit="d => OnSubmitAction(card.DataSourceId, "save", d)" />
                break;
            case CardType.Stream:
                <StreamCard Schema="@((StreamSchema)card.Schema)" Data="@data" />
                break;
            case CardType.Chat:
                <ChatCard Schema="@((ChatSchema)card.Schema)" Data="@data"
                          OnSubmit="d => OnSubmitAction(card.DataSourceId, "send", d)" />
                break;
            case CardType.Tree:
                <TreeCard Schema="@((TreeSchema)card.Schema)" Data="@data"
                          OnNodeSelect="id => pageContext?.Emit("node-selected", JsonValue.Create(id))" />
                break;
            case CardType.Detail:
                <DetailCard Schema="@((DetailSchema)card.Schema)" Data="@data"
                            OnSubmit="d => OnSubmitAction(card.DataSourceId, "update", d)" />
                break;
        }
    };

    private async Task OnQuery(string dsId, DataQuery query)
    {
        await dsManager.FetchAsync(dsId, query);
        StateHasChanged();
    }

    private async Task OnRowAction(string dsId, (string ActionId, JsonNode Row) e)
    {
        await dsManager.SubmitAsync(dsId, e.ActionId, e.Row);
        await dsManager.FetchAsync(dsId);
        StateHasChanged();
    }

    private async Task OnSubmitAction(string dsId, string action, JsonNode? data)
    {
        await dsManager.SubmitAsync(dsId, action, data);
        await dsManager.FetchAsync(dsId);
        StateHasChanged();
    }

    private void Cleanup()
    {
        dsManager.OnDataChanged -= OnDataSourceChanged;
        dsManager.Dispose();
        pageContext?.Dispose();
    }

    public void Dispose() => Cleanup();
}
```

- [ ] **Step 2: Verify compilation**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/WebUI/Components/Shell/DynamicPage.razor
git commit -m "feat(webui): add DynamicPage component with route matching and card dispatch"
```

---

### Task 13: Integration — PluginLoader + ProviderRegistry + Program.cs

**Files:**
- Modify: `AgentCoreProcessor/Tool/Host/PluginLoader.cs`
- Modify: `AgentCoreProcessor/Program.cs`

- [ ] **Step 1: Extend PluginLoader to discover IWebUIProvider**

Add to `PluginLoader.cs` — new discovery method and registration in `LoadPlugin`:

```csharp
// Add field at class level:
private readonly ProviderRegistry? _providerRegistry;

// Modify constructor to accept optional ProviderRegistry:
public PluginLoader(IToolContext toolContext, WebUI.Shell.ProviderRegistry? providerRegistry = null)
{
    this.toolContext = toolContext;
    _providerRegistry = providerRegistry;
}

// Add discovery method:
private static List<Type> DiscoverProviderTypes(Assembly assembly)
{
    var iProviderType = typeof(AgentLilara.PluginSDK.WebUI.IWebUIProvider);
    return assembly.GetExportedTypes()
        .Where(t => t.IsClass && !t.IsAbstract && iProviderType.IsAssignableFrom(t))
        .ToList();
}

// In LoadPlugin method, after component registration, add:
var providerTypes = DiscoverProviderTypes(assembly);
foreach (var type in providerTypes)
{
    try
    {
        var provider = (AgentLilara.PluginSDK.WebUI.IWebUIProvider)Activator.CreateInstance(type)!;
        var attr = type.GetCustomAttribute<AgentLilara.PluginSDK.WebUI.WebUIProviderAttribute>();
        if (_providerRegistry?.Register(provider, attr?.BuiltIn ?? false) == true)
        {
            entry.ProviderIds.Add(provider.Id);
        }
    }
    catch (Exception ex)
    {
        FrameworkLogger.Log("PluginLoader", $"Provider 实例化失败 {type.Name}: {ex.Message}");
    }
}

// Add ProviderIds to PluginEntry:
public List<string> ProviderIds { get; set; } = new();

// In UnloadAll, add provider unregistration:
foreach (var id in entry.ProviderIds)
    _providerRegistry?.Unregister(id);
```

- [ ] **Step 2: Register ProviderRegistry in Program.cs**

Add after existing service registrations (around line 274):

```csharp
builder.Services.AddSingleton<AgentCoreProcessor.WebUI.Shell.ProviderRegistry>();
```

And pass it to PluginLoader construction (wherever PluginLoader is instantiated in MasterEngine.InitAsync or similar).

- [ ] **Step 3: Verify compilation**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/Tool/Host/PluginLoader.cs AgentCoreProcessor/Program.cs
git commit -m "feat(webui): integrate ProviderRegistry with PluginLoader and DI"
```

---

### Task 14: Integration — NavMenu reads from ProviderRegistry

**Files:**
- Modify: `AgentCoreProcessor/WebUI/Components/Layout/NavMenu.razor`

- [ ] **Step 1: Update NavMenu to use ProviderRegistry**

Replace the static `NavConfig.Items` iteration with ProviderRegistry-driven nav tree. Keep the existing sidebar structure (brand, divider, footer) but replace the `<ul>` content:

```razor
@* Replace: @foreach (var item in NavConfig.Items) block *@
@* With: *@
@inject ProviderRegistry Registry

@* In the <ul class="sidebar-nav"> section: *@
@foreach (var group in navGroups)
{
    @if (group.IsSinglePage && group.Pages.Count == 1)
    {
        var p = group.Pages[0];
        <li @key="p.Route">
            <NavLink class="nav-link" href="@($"/p/{p.Route}")"
                     Match="@(p.Route == "" ? NavLinkMatch.All : NavLinkMatch.Prefix)">
                <i class="bi @(p.Meta.Icon ?? "bi-circle")"></i> @p.Meta.Title
            </NavLink>
        </li>
    }
    else
    {
        <NavSection @key="group.Name" Item="@ToNavItem(group)" ExpandedSet="expandedSet" Version="version" />
    }
}

@* Also keep the static bottom section for page management: *@
<li>
    <NavLink class="nav-link" href="/p/admin/providers" Match="NavLinkMatch.All">
        <i class="bi bi-grid-3x3-gap"></i> 页面管理
    </NavLink>
</li>

@* Add to @code block: *@
private List<NavGroup> navGroups = new();

protected override void OnInitialized()
{
    navGroups = Registry.BuildNavTree();
    Registry.OnChanged += OnRegistryChanged;
    // ... existing code ...
}

private void OnRegistryChanged()
{
    navGroups = Registry.BuildNavTree();
    InvokeAsync(StateHasChanged);
}

private NavItem ToNavItem(NavGroup group) => new()
{
    Title = group.Name,
    Icon = group.Icon ?? "bi-folder",
    Children = group.Pages.Select(p => new NavItem
    {
        Title = p.Meta.Title,
        Href = $"/p/{p.Route}",
        Icon = p.Meta.Icon ?? ""
    }).ToList()
};

// In Dispose:
Registry.OnChanged -= OnRegistryChanged;
```

Note: During migration, keep the old `NavConfig.Items` as fallback for pages not yet migrated. The `/p/` prefix distinguishes dynamic pages from legacy routes.

- [ ] **Step 2: Verify compilation**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/WebUI/Components/Layout/NavMenu.razor
git commit -m "feat(webui): NavMenu reads navigation from ProviderRegistry"
```

---

### Task 15: Smoke Test — Create a Minimal Test Provider

**Files:**
- Create: `AgentCoreProcessor/WebUI/Providers/TestProvider.cs`

- [ ] **Step 1: Create a minimal built-in provider for verification**

```csharp
// AgentCoreProcessor/WebUI/Providers/TestProvider.cs
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class TestProvider : IWebUIProvider
{
    public string Id => "test-provider";
    public string DisplayName => "测试 Provider";

    public IReadOnlyList<PageDefinition> Pages { get; } = new List<PageDefinition>
    {
        new()
        {
            Route = "test/status",
            Meta = new PageMeta { Title = "测试页面", Icon = "bi-bug", Group = "测试", Order = 0 },
            Cards = new List<CardDefinition>
            {
                new()
                {
                    Id = "test-status",
                    Type = CardType.Status,
                    DataSourceId = "test-data",
                    Title = "系统状态（测试）",
                    Schema = new StatusSchema
                    {
                        Fields = new()
                        {
                            new() { Field = "status", Label = "状态", Type = StatusFieldType.Indicator },
                            new() { Field = "uptime", Label = "运行时间" },
                            new() { Field = "version", Label = "版本", Type = StatusFieldType.Badge }
                        },
                        Actions = new()
                        {
                            new() { Id = "refresh", Label = "刷新", Icon = "bi-arrow-clockwise" }
                        }
                    },
                    Layout = new CardLayout { PreferredCols = 6 }
                },
                new()
                {
                    Id = "test-table",
                    Type = CardType.Table,
                    DataSourceId = "test-list",
                    Title = "测试列表",
                    Schema = new TableSchema
                    {
                        Columns = new()
                        {
                            new() { Field = "id", Header = "ID", Width = "60px" },
                            new() { Field = "name", Header = "名称" },
                            new() { Field = "time", Header = "时间", Format = ColumnFormat.DateTime }
                        }
                    },
                    Layout = new CardLayout { PreferredCols = 6 }
                }
            },
            DataSources = new List<DataSourceDefinition>
            {
                new() { Id = "test-data", Source = new TestStatusDataSource() },
                new() { Id = "test-list", Source = new TestListDataSource() }
            }
        }
    };
}

internal class TestStatusDataSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var data = new JsonObject
        {
            ["status"] = "running",
            ["uptime"] = $"{(int)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMinutes} 分钟",
            ["version"] = "1.0.0"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true, Message = "OK" });
}

internal class TestListDataSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var arr = new JsonArray();
        for (int i = 1; i <= 5; i++)
        {
            arr.Add(new JsonObject
            {
                ["id"] = i,
                ["name"] = $"测试项目 {i}",
                ["time"] = DateTime.Now.AddMinutes(-i * 10).ToString("O")
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = 5 });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}
```

- [ ] **Step 2: Register TestProvider at startup**

In the startup code (after ProviderRegistry is created), manually register the test provider:

```csharp
var providerRegistry = app.Services.GetRequiredService<ProviderRegistry>();
providerRegistry.Register(new TestProvider(), builtIn: true);
```

- [ ] **Step 3: Verify compilation and run**

Run: `dotnet build && dotnet run`
Expected: Build succeeded, navigate to `http://localhost:5000/p/test/status` and see the test page with Status + Table cards.

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/WebUI/Providers/TestProvider.cs
git commit -m "feat(webui): add TestProvider for smoke testing card system"
```

---

### Task 16: Final — Build Verification and Cleanup

**Files:**
- Modify: `docs/architecture-map.md` (update WebUI section)

- [ ] **Step 1: Full build verification**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 2: Run the application and verify test page**

Run: `dotnet run --project AgentCoreProcessor/AgentCoreProcessor.csproj`
Navigate to: `http://localhost:5000/p/test/status`
Expected: Page renders with two cards side by side — Status card showing "running" + Table card with 5 rows.

- [ ] **Step 3: Update architecture docs**

Add to `docs/architecture-map.md` WebUI section:

```markdown
卡片式数据驱动系统:
  SDK 接口: IWebUIProvider / IDataSource / CardSchema (7种) / IPageContext
  Shell: ProviderRegistry + DynamicPage + CardGrid + CardHost + 7种卡片渲染器
  路由: /p/{route} → ProviderRegistry.FindPage → DynamicPage 渲染
  数据层: DataSourceManager (Fetch重试 + Subscribe推送)
  Provider 发现: PluginLoader 扫描 IWebUIProvider 实现类
  热重载: ALC 卸载 → 反注册 → 重新加载 → 注册 → 侧边栏刷新
```

- [ ] **Step 4: Commit**

```bash
git add docs/architecture-map.md
git commit -m "docs: update architecture map with WebUI card system"
```
