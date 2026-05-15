# 日志追踪可视化页面 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 WebUI 日志追踪页面，以 git-tree 风格可视化展示信号因果链，支持左右分离滚动、SVG 统一线条渲染、因果链高亮交互。

**Architecture:** 独立 Razor 组件（不走卡片系统），通过 Provider 注册导航入口。左侧 SVG 图区（横滚）+ 右侧事件文本（固定），数据来自 logs.db events 表。前端用 JS interop 处理 SVG 渲染和交互。

**Tech Stack:** .NET 8 Blazor Server, SVG, JavaScript interop, SQLite (logs.db)

---

## 文件结构

```
AgentCoreProcessor/WebUI/
├── Components/Pages/
│   └── LogTrace.razor              — 页面组件（路由 @page "/logs/trace"）
├── Providers/
│   └── LogTraceProvider.cs         — Provider 注册（导航入口）
├── Services/
│   └── LogTraceService.cs          — 数据查询+视图模型转换
└── wwwroot/
    ├── css/log-trace.css           — 可视化专用样式（语义色板，明/暗两档）
    └── js/log-trace.js             — SVG 渲染 + 因果链高亮交互
```

---

### Task 1: LogTraceProvider — 导航注册

**Files:**
- Create: `AgentCoreProcessor/WebUI/Providers/LogTraceProvider.cs`
- Modify: `AgentCoreProcessor/Program.cs` (注册 provider)

- [ ] **Step 1: 创建 LogTraceProvider**

```csharp
// AgentCoreProcessor/WebUI/Providers/LogTraceProvider.cs
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class LogTraceProvider : IWebUIProvider
{
    public string Id => "log-trace";
    public string DisplayName => "信号追踪";

    public IReadOnlyList<PageDefinition> Pages { get; } = new List<PageDefinition>
    {
        new()
        {
            Route = "logs/trace",
            Meta = new PageMeta { Title = "信号追踪", Icon = "bi-diagram-3", Group = "", Order = 5 },
            Cards = new List<CardDefinition>(),
            DataSources = new List<DataSourceDefinition>()
        }
    };
}
```

- [ ] **Step 2: 在 Program.cs 注册 provider**

在 TestProvider 注册附近添加：
```csharp
providerRegistry.Register(new LogTraceProvider(), builtIn: true);
```

- [ ] **Step 3: 修改 DynamicPage 支持自定义页面路由**

DynamicPage 当前只渲染卡片。对于 LogTrace 页面，需要在 `DynamicPage.razor` 中检测路由为 `logs/trace` 时渲染独立组件，或者直接让 LogTrace 用 `@page "/p/logs/trace"` 路由（与 DynamicPage 的 catch-all 路由冲突）。

**方案：** LogTrace 使用 `@page "/logs/trace"` 独立路由（不走 `/p/` 前缀），Provider 仅负责导航注册。NavMenu 已经支持混合模式（静态 + Provider），只需确保 Provider 的 route 在 NavMenu 中生成正确的 href。

修改 `NavMenu.razor` 中 Provider 页面链接逻辑：如果 route 以 `logs/` 开头，直接用 `/{route}` 而非 `/p/{route}`。

或者更简单：在 PageMeta 中加一个 `DirectRoute` 标记，NavMenu 据此决定 href 前缀。

**最终方案（最小改动）：** LogTrace 用 `@page "/logs/trace"` 独立路由。Provider 的 PageMeta 设置 route 为 `logs/trace`，NavMenu 中对 IsSinglePage 的 Provider 页面，如果 route 不以 `test/` 开头就用 `"/{route}"` 而非 `"/p/{route}"`。

实际上更简单的做法：直接在 PageDefinition 上加一个 `CustomRoute` 属性，NavMenu 优先使用它。但这改 SDK 了。

**最终最终方案：** 不改 SDK。LogTrace 同时注册 `@page "/logs/trace"` 和 `@page "/p/logs/trace"`，Provider route 保持 `logs/trace`，NavMenu 生成 `/p/logs/trace` 链接，Blazor 路由匹配到 LogTrace 组件。DynamicPage 的 catch-all 也会匹配，但 LogTrace 的精确路由优先级更高。

- [ ] **Step 4: 验证编译通过**

```bash
taskkill /IM AgentCoreProcessor.exe /F 2>/dev/null; dotnet build
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(webui): add LogTraceProvider for navigation registration"
```

---

### Task 2: LogTraceService — 数据查询层

**Files:**
- Create: `AgentCoreProcessor/WebUI/Services/LogTraceService.cs`

- [ ] **Step 1: 创建 LogTraceService**

职责：从 ILogQuery 获取数据，转换为前端渲染所需的视图模型。

```csharp
// AgentCoreProcessor/WebUI/Services/LogTraceService.cs
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.WebUI.Services;

internal class LogTraceService
{
    private readonly ILogQuery _query;

    public LogTraceService(ILogQuery query) => _query = query;

    public List<SignalSummary> GetSignalList(int limit = 50)
    {
        var roots = _query.GetSignalList(limit);
        return roots.Select(e => new SignalSummary
        {
            SignalId = e.SignalId,
            Scope = e.Scope,
            Name = e.Name,
            Timestamp = e.Timestamp,
            HasOpenSpans = false
        }).ToList();
    }

    public TraceViewModel GetTrace(string signalId)
    {
        var events = _query.GetBySignal(signalId);
        return BuildViewModel(events);
    }

    public TraceViewModel GetRecentTrace(int limit = 500)
    {
        var events = _query.GetRecent(limit);
        return BuildViewModel(events);
    }

    private TraceViewModel BuildViewModel(List<LogEvent> events)
    {
        var scopes = events.Select(e => e.Scope).Distinct().ToList();
        var rows = new List<TraceRow>();

        foreach (var evt in events.OrderBy(e => e.Timestamp).ThenBy(e => e.Id))
        {
            rows.Add(new TraceRow
            {
                Id = evt.Id,
                SignalId = evt.SignalId,
                Scope = evt.Scope,
                ParentId = evt.ParentId,
                SpanId = evt.SpanId,
                Type = evt.Type,
                Level = evt.Level,
                Timestamp = evt.Timestamp,
                Name = evt.Name,
                Detail = evt.Detail,
                GroupName = evt.GroupName
            });
        }

        return new TraceViewModel { Scopes = scopes, Rows = rows };
    }
}

internal class SignalSummary
{
    public string SignalId { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Name { get; set; } = "";
    public long Timestamp { get; set; }
    public bool HasOpenSpans { get; set; }
}

internal class TraceViewModel
{
    public List<string> Scopes { get; set; } = new();
    public List<TraceRow> Rows { get; set; } = new();
}

internal class TraceRow
{
    public long Id { get; set; }
    public string SignalId { get; set; } = "";
    public string Scope { get; set; } = "";
    public long? ParentId { get; set; }
    public string? SpanId { get; set; }
    public string Type { get; set; } = "";
    public int Level { get; set; }
    public long Timestamp { get; set; }
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
    public string GroupName { get; set; } = "";
}
```

- [ ] **Step 2: 在 Program.cs 注册为 Scoped 服务**

```csharp
builder.Services.AddScoped<LogTraceService>(sp => new LogTraceService(logQuery));
```

- [ ] **Step 3: 验证编译**

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat(webui): add LogTraceService for trace data queries"
```

---

### Task 3: CSS 样式 — 可视化语义色板

**Files:**
- Create: `AgentCoreProcessor/WebUI/wwwroot/css/log-trace.css`
- Modify: `AgentCoreProcessor/WebUI/Components/App.razor` (引入 CSS)

- [ ] **Step 1: 创建 log-trace.css**

固定语义色板，明/暗两档。不跟随主题风格化（无玻璃态/角框）。

```css
/* log-trace.css — 信号追踪可视化 */
:root {
    --vis-ok: #3ecf8e;
    --vis-error: #ef4444;
    --vis-info: #60a5fa;
    --vis-debug: #6b7280;
    --vis-warn: #f5a623;
    --vis-line: #4b5068;
    --vis-line-cross: rgba(124,130,152,0.5);
    --vis-text: #cdd2dc;
    --vis-text-dim: #949ab0;
    --vis-bg: #161922;
    --vis-node-size: 9px;
    --vis-line-width: 1.5px;
    --vis-row-height: 26px;
    --vis-slot-width: 14px;
    --vis-col-separator: rgba(75,80,104,0.3);
}

[data-theme="light"] {
    --vis-ok: #16a34a;
    --vis-error: #dc2626;
    --vis-info: #2563eb;
    --vis-debug: #9ca3af;
    --vis-warn: #d97706;
    --vis-line: #d1d5db;
    --vis-line-cross: rgba(156,163,175,0.6);
    --vis-text: #374151;
    --vis-text-dim: #6b7280;
    --vis-bg: #ffffff;
    --vis-col-separator: rgba(209,213,219,0.5);
}

.log-trace-page { display: flex; flex-direction: column; height: 100%; }
.log-trace-toolbar { padding: 0.5rem 1rem; border-bottom: 1px solid var(--border); display: flex; gap: 0.5rem; align-items: center; flex-shrink: 0; }
.log-trace-body { display: flex; flex: 1; overflow: hidden; }

.log-trace-graph { flex-shrink: 0; overflow-x: auto; overflow-y: auto; position: relative; background: var(--vis-bg); }
.log-trace-text { flex: 1; overflow-y: auto; min-width: 300px; border-left: 1px solid var(--border); }

.log-trace-header { display: flex; border-bottom: 1px solid var(--vis-col-separator); padding: 0.4rem 0; font-size: 12px; color: var(--vis-text-dim); position: sticky; top: 0; background: var(--vis-bg); z-index: 10; }
.log-trace-col-header { text-align: center; flex-shrink: 0; border-right: 1px dashed var(--vis-col-separator); }

.trace-row { display: flex; align-items: center; height: var(--vis-row-height); position: relative; cursor: pointer; }
.trace-row:hover { background: rgba(255,255,255,0.03); }

.trace-text-row { height: var(--vis-row-height); display: flex; align-items: center; padding: 0 0.75rem; font-size: 13px; color: var(--vis-text); cursor: pointer; }
.trace-text-row:hover { background: rgba(255,255,255,0.03); }
.trace-text-row .time { width: 80px; flex-shrink: 0; font-size: 11px; color: var(--vis-text-dim); font-family: 'Cascadia Code', monospace; }
.trace-text-row .name { flex: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.trace-text-row .tag { font-size: 11px; color: var(--vis-text-dim); margin-left: 0.5rem; }
.trace-text-row .dur { font-size: 11px; color: var(--vis-ok); margin-left: 0.5rem; }

/* Hover dim */
.log-trace-body.has-hover .trace-row.dimmed,
.log-trace-body.has-hover .trace-text-row.dimmed { opacity: 0.15; }
.log-trace-body.has-hover .trace-row.dimmed svg line,
.log-trace-body.has-hover .trace-row.dimmed svg circle,
.log-trace-body.has-hover .trace-row.dimmed svg rect { opacity: 0.15; }
.log-trace-body.has-hover svg .cross-line.dimmed { opacity: 0.12; }
.trace-row, .trace-text-row { transition: opacity 0.15s; }
```

- [ ] **Step 2: 在 App.razor 引入 CSS**

在已有的 `<link href="css/app.css" ...>` 后添加：
```html
<link href="css/log-trace.css" rel="stylesheet" />
```

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(webui): add log-trace.css with semantic color palette"
```

---

### Task 4: LogTrace.razor — 页面骨架

**Files:**
- Create: `AgentCoreProcessor/WebUI/Components/Pages/LogTrace.razor`

- [ ] **Step 1: 创建 LogTrace.razor 页面骨架**

```razor
@page "/logs/trace"
@page "/p/logs/trace"
@attribute [Microsoft.AspNetCore.Authorization.Authorize]
@using AgentCoreProcessor.WebUI.Services
@inject LogTraceService TraceService
@inject IJSRuntime JS
@implements IAsyncDisposable

<div class="log-trace-page">
    <div class="log-trace-toolbar">
        <select class="form-select form-select-sm" style="width:auto;" @bind="_selectedSignal" @bind:after="LoadTrace">
            <option value="">最近事件</option>
            @foreach (var sig in _signals)
            {
                <option value="@sig.SignalId">@FormatSignalOption(sig)</option>
            }
        </select>
        <button class="btn btn-sm btn-outline-secondary" @onclick="Refresh">
            <i class="bi bi-arrow-clockwise"></i>
        </button>
    </div>

    <div class="log-trace-body" @ref="_bodyRef">
        <div class="log-trace-graph" @ref="_graphRef">
            @* SVG rendered by JS *@
        </div>
        <div class="log-trace-text" @ref="_textRef">
            @* Text rows rendered by JS for sync scroll *@
        </div>
    </div>
</div>

@code {
    private string _selectedSignal = "";
    private List<SignalSummary> _signals = new();
    private TraceViewModel? _trace;
    private ElementReference _bodyRef;
    private ElementReference _graphRef;
    private ElementReference _textRef;
    private IJSObjectReference? _jsModule;

    protected override async Task OnInitializedAsync()
    {
        _signals = TraceService.GetSignalList(50);
        await LoadTrace();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>(
                "import", "./js/log-trace.js");
            await RenderGraph();
        }
    }

    private async Task LoadTrace()
    {
        _trace = string.IsNullOrEmpty(_selectedSignal)
            ? TraceService.GetRecentTrace(500)
            : TraceService.GetTrace(_selectedSignal);
        await RenderGraph();
    }

    private async Task Refresh()
    {
        _signals = TraceService.GetSignalList(50);
        await LoadTrace();
    }

    private async Task RenderGraph()
    {
        if (_jsModule == null || _trace == null) return;
        await _jsModule.InvokeVoidAsync("renderTrace",
            _graphRef, _textRef, _bodyRef, _trace.Scopes, _trace.Rows);
    }

    private string FormatSignalOption(SignalSummary sig)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(sig.Timestamp).LocalDateTime;
        return $"{time:HH:mm:ss} {sig.Name} ({sig.SignalId[..8]})";
    }

    public async ValueTask DisposeAsync()
    {
        if (_jsModule != null)
        {
            await _jsModule.InvokeVoidAsync("dispose");
            await _jsModule.DisposeAsync();
        }
    }
}
```

- [ ] **Step 2: 验证编译**

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(webui): add LogTrace.razor page skeleton"
```

---

### Task 5: log-trace.js — SVG 渲染核心

**Files:**
- Create: `AgentCoreProcessor/WebUI/wwwroot/js/log-trace.js`

这是最复杂的任务。JS 模块负责：
1. 根据 scopes 和 rows 数据计算列布局（每列 slot 数、宽度）
2. 渲染 SVG 图区（竖线 + 节点 + 斜线全部 SVG）
3. 渲染右侧文本行（与图区行高对齐）
4. 同步左右滚动（垂直方向）
5. 因果链高亮交互

- [ ] **Step 1: 创建 log-trace.js 模块结构**

```javascript
// log-trace.js — SVG 信号追踪渲染器
const ROW_HEIGHT = 26;
const SLOT_WIDTH = 14;
const NODE_SIZE = 9;
const LINE_WIDTH = 1.5;
const CROSS_LINE_WIDTH = 2.5;
const COL_PADDING = 12; // 列两侧 padding
const MIN_COL_WIDTH = 180;

let state = null; // 当前渲染状态

export function renderTrace(graphEl, textEl, bodyEl, scopes, rows) {
    state = buildState(scopes, rows);
    renderGraph(graphEl);
    renderText(textEl);
    setupInteraction(graphEl, textEl, bodyEl);
    syncScroll(graphEl, textEl);
}

export function dispose() {
    if (state?.cleanup) state.cleanup();
    state = null;
}
```

- [ ] **Step 2: 实现 buildState — 列布局计算**

```javascript
function buildState(scopes, rows) {
    // 为每个 scope 计算需要的 slot 数（基于最大嵌套深度）
    const scopeSlots = {};
    const scopeOrder = [];

    for (const scope of scopes) {
        scopeSlots[scope] = 1;
        scopeOrder.push(scope);
    }

    // 计算每个 scope 的最大并发 span 深度
    const openStacks = {}; // scope -> current open stack depth
    for (const row of rows) {
        if (!openStacks[row.scope]) openStacks[row.scope] = 0;
        if (row.type === 'open') {
            openStacks[row.scope]++;
            scopeSlots[row.scope] = Math.max(scopeSlots[row.scope], openStacks[row.scope]);
        } else if (row.type === 'close') {
            openStacks[row.scope] = Math.max(0, openStacks[row.scope] - 1);
        }
    }

    // 限制最大 slot 数为 5
    for (const scope of scopes) {
        scopeSlots[scope] = Math.min(scopeSlots[scope], 5);
    }

    // 计算列宽和 x 偏移
    const colLayout = [];
    let xOffset = 0;
    for (const scope of scopeOrder) {
        const slots = scopeSlots[scope];
        const width = Math.max(MIN_COL_WIDTH, slots * SLOT_WIDTH + COL_PADDING * 2);
        colLayout.push({ scope, slots, width, x: xOffset });
        xOffset += width;
    }

    // 构建因果树（parent_id -> children）
    const byId = {};
    const childrenOf = {};
    for (const row of rows) {
        byId[row.id] = row;
        if (row.parentId != null) {
            if (!childrenOf[row.parentId]) childrenOf[row.parentId] = [];
            childrenOf[row.parentId].push(row.id);
        }
    }

    // 配对 open/close（通过 spanId）
    const openBySpan = {};
    const closeToOpen = {};
    for (const row of rows) {
        if (row.type === 'open' && row.spanId) openBySpan[row.spanId] = row.id;
        if (row.type === 'close' && row.spanId && openBySpan[row.spanId]) {
            closeToOpen[row.id] = openBySpan[row.spanId];
        }
    }

    return { scopes: scopeOrder, rows, colLayout, totalWidth: xOffset,
             byId, childrenOf, closeToOpen, scopeSlots };
}
```

- [ ] **Step 3: 实现 renderGraph — SVG 绘制**

核心逻辑：
- 每行一个 `<g>` 元素，y = rowIndex * ROW_HEIGHT
- 竖线：追踪每个 scope 每个 slot 的 open/close 状态，在 open 和 close 之间画竖线段
- 节点：根据 type 画不同形状（circle/ring/diamond）
- 斜线：跨 scope 的因果连接（parent 在不同 scope 时画斜线）

```javascript
function renderGraph(graphEl) {
    const { rows, colLayout, totalWidth, scopeSlots } = state;
    const totalHeight = rows.length * ROW_HEIGHT;

    // 清空
    graphEl.innerHTML = '';

    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('width', totalWidth);
    svg.setAttribute('height', totalHeight);
    svg.style.display = 'block';

    // 列分隔背景
    for (let i = 0; i < colLayout.length; i++) {
        const col = colLayout[i];
        if (i > 0) {
            const line = createSvgEl('line', {
                x1: col.x, y1: 0, x2: col.x, y2: totalHeight,
                stroke: 'var(--vis-col-separator)', 'stroke-width': 1,
                'stroke-dasharray': '4,4', class: 'col-sep'
            });
            svg.appendChild(line);
        }
    }

    // 计算每行每个 slot 的线段状态 + 节点
    const slotState = computeSlotState();

    // 渲染竖线
    renderVerticalLines(svg, slotState);

    // 渲染节点
    renderNodes(svg);

    // 渲染斜线（跨 scope 因果连接）
    renderCrossLines(svg);

    graphEl.appendChild(svg);
    state.svg = svg;
}
```

- [ ] **Step 4: 实现 computeSlotState**

追踪每个 scope 的 slot 占用情况，确定每行每个 slot 是否有竖线经过。

```javascript
function computeSlotState() {
    const { rows, colLayout, scopes, scopeSlots } = state;
    // slotState[rowIdx][scopeIdx][slotIdx] = signalId | null
    const result = [];
    // 每个 scope 的 slot 栈：哪些 span 正在占用哪个 slot
    const activeSlots = {}; // scope -> [{spanId, signalId, slotIdx}]
    for (const scope of scopes) activeSlots[scope] = [];

    for (let ri = 0; ri < rows.length; ri++) {
        const row = rows[ri];
        const scopeIdx = scopes.indexOf(row.scope);

        if (row.type === 'open') {
            // 分配下一个可用 slot
            const used = activeSlots[row.scope].map(s => s.slotIdx);
            let slot = 0;
            while (used.includes(slot) && slot < scopeSlots[row.scope]) slot++;
            activeSlots[row.scope].push({ spanId: row.spanId, signalId: row.signalId, slotIdx: slot });
            row._slotIdx = slot;
            row._scopeIdx = scopeIdx;
        } else if (row.type === 'close') {
            const idx = activeSlots[row.scope].findIndex(s => s.spanId === row.spanId);
            if (idx >= 0) {
                row._slotIdx = activeSlots[row.scope][idx].slotIdx;
                activeSlots[row.scope].splice(idx, 1);
            }
            row._scopeIdx = scopeIdx;
        } else {
            // event: 放在最深的活跃 slot
            const active = activeSlots[row.scope];
            row._slotIdx = active.length > 0 ? active[active.length - 1].slotIdx : 0;
            row._scopeIdx = scopeIdx;
        }

        // 记录当前行所有 scope 的活跃线
        const rowState = [];
        for (const scope of scopes) {
            const slots = [];
            for (let s = 0; s < scopeSlots[scope]; s++) {
                const active = activeSlots[scope].find(a => a.slotIdx === s);
                slots.push(active ? active.signalId : null);
            }
            rowState.push(slots);
        }
        result.push(rowState);
    }

    state.slotState = result;
    return result;
}
```

- [ ] **Step 5: 实现 renderVerticalLines + renderNodes + renderCrossLines**

（具体 SVG 元素创建逻辑，基于 slotState 和 row 数据）

```javascript
function renderVerticalLines(svg, slotState) {
    const { rows, colLayout, scopes, scopeSlots } = state;
    for (let ri = 0; ri < rows.length; ri++) {
        const y = ri * ROW_HEIGHT;
        for (let si = 0; si < scopes.length; si++) {
            const col = colLayout[si];
            for (let slot = 0; slot < scopeSlots[scopes[si]]; slot++) {
                const sigId = slotState[ri][si][slot];
                if (!sigId) continue;
                const x = col.x + COL_PADDING + slot * SLOT_WIDTH + SLOT_WIDTH / 2;
                // 判断是否是 open/close 行（半线）
                const row = rows[ri];
                const isThisSlot = row._scopeIdx === si && row._slotIdx === slot;
                let y1 = y, y2 = y + ROW_HEIGHT;
                if (isThisSlot && row.type === 'open') y1 = y + ROW_HEIGHT / 2;
                if (isThisSlot && row.type === 'close') y2 = y + ROW_HEIGHT / 2;

                const line = createSvgEl('line', {
                    x1: x, y1, x2: x, y2,
                    stroke: 'var(--vis-line)', 'stroke-width': LINE_WIDTH,
                    'data-row': ri, 'data-signal': sigId, class: 'v-line'
                });
                svg.appendChild(line);
            }
        }
    }
}

function renderNodes(svg) {
    const { rows, colLayout } = state;
    for (let ri = 0; ri < rows.length; ri++) {
        const row = rows[ri];
        const col = colLayout[row._scopeIdx];
        const x = col.x + COL_PADDING + row._slotIdx * SLOT_WIDTH + SLOT_WIDTH / 2;
        const y = ri * ROW_HEIGHT + ROW_HEIGHT / 2;

        if (row.type === 'open' || row.type === 'close') {
            // 圆环节点
            const isPaired = row.type === 'close' || hasPairedClose(row);
            const isOrigin = row.type === 'open' && row.parentId == null;
            const r = NODE_SIZE / 2;
            const circle = createSvgEl('circle', {
                cx: x, cy: y, r: r,
                fill: isOrigin ? 'var(--vis-bg)' : (isPaired ? 'var(--vis-bg)' : 'var(--vis-bg)'),
                stroke: isOrigin ? 'var(--vis-info)' : (isPaired ? 'var(--vis-ok)' : 'var(--vis-error)'),
                'stroke-width': 2,
                'data-row': ri, 'data-id': row.id, class: 'node'
            });
            svg.appendChild(circle);
            if (isOrigin) {
                // 双圈
                const outer = createSvgEl('circle', {
                    cx: x, cy: y, r: r + 2.5,
                    fill: 'none', stroke: 'var(--vis-info)', 'stroke-width': 1,
                    'data-row': ri, class: 'node'
                });
                svg.appendChild(outer);
            }
        } else if (row.type === 'event') {
            // 实心圆
            const color = row.level === 0 ? 'var(--vis-debug)' : 'var(--vis-info)';
            const circle = createSvgEl('circle', {
                cx: x, cy: y, r: NODE_SIZE / 2 - 1,
                fill: color, stroke: 'none',
                'data-row': ri, 'data-id': row.id, class: 'node'
            });
            svg.appendChild(circle);
        } else if (row.type === 'absorption') {
            // 菱形
            const s = 4;
            const d = `M${x} ${y-s} L${x+s} ${y} L${x} ${y+s} L${x-s} ${y} Z`;
            const diamond = createSvgEl('path', {
                d, fill: 'var(--vis-warn)', stroke: 'none',
                'data-row': ri, 'data-id': row.id, class: 'node diamond'
            });
            svg.appendChild(diamond);
        }
    }
}

function renderCrossLines(svg) {
    const { rows, colLayout } = state;
    // 跨 scope 的因果连接：parent 在不同 scope 时画斜线
    for (let ri = 0; ri < rows.length; ri++) {
        const row = rows[ri];
        if (row.parentId == null) continue;
        const parent = state.byId[row.parentId];
        if (!parent || parent.scope === row.scope) continue;

        const parentIdx = rows.indexOf(parent);
        if (parentIdx < 0) continue;

        const col1 = colLayout[parent._scopeIdx];
        const col2 = colLayout[row._scopeIdx];
        const x1 = col1.x + COL_PADDING + parent._slotIdx * SLOT_WIDTH + SLOT_WIDTH / 2;
        const y1 = parentIdx * ROW_HEIGHT + ROW_HEIGHT / 2;
        const x2 = col2.x + COL_PADDING + row._slotIdx * SLOT_WIDTH + SLOT_WIDTH / 2;
        const y2 = ri * ROW_HEIGHT + ROW_HEIGHT / 2;

        const line = createSvgEl('line', {
            x1, y1, x2, y2,
            stroke: 'var(--vis-line-cross)', 'stroke-width': CROSS_LINE_WIDTH,
            'data-from': parent.id, 'data-to': row.id, class: 'cross-line'
        });
        svg.appendChild(line);
    }
}

function hasPairedClose(openRow) {
    const { rows } = state;
    return rows.some(r => r.type === 'close' && r.spanId === openRow.spanId);
}

function createSvgEl(tag, attrs) {
    const el = document.createElementNS('http://www.w3.org/2000/svg', tag);
    for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
    return el;
}
```

- [ ] **Step 6: 实现 renderText — 右侧文本行**

```javascript
function renderText(textEl) {
    const { rows } = state;
    textEl.innerHTML = '';
    for (let ri = 0; ri < rows.length; ri++) {
        const row = rows[ri];
        const div = document.createElement('div');
        div.className = 'trace-text-row';
        div.dataset.row = ri;
        div.dataset.id = row.id;

        const time = new Date(row.timestamp).toLocaleTimeString('zh-CN', { hour12: false, fractionalSecondDigits: 1 });
        const typeLabel = row.type === 'open' ? '[开始]' : row.type === 'close' ? '[完成]' : '';
        const name = row.name || '';

        div.innerHTML = `<span class="time">${time}</span><span class="name">${typeLabel} ${name}</span>`;
        if (row.detail) {
            div.innerHTML += `<span class="tag">${truncate(row.detail, 40)}</span>`;
        }
        textEl.appendChild(div);
    }
}

function truncate(s, max) {
    return s.length > max ? s.slice(0, max) + '…' : s;
}
```

- [ ] **Step 7: 实现 syncScroll — 左右垂直同步**

```javascript
function syncScroll(graphEl, textEl) {
    let syncing = false;
    graphEl.addEventListener('scroll', () => {
        if (syncing) return;
        syncing = true;
        textEl.scrollTop = graphEl.scrollTop;
        syncing = false;
    });
    textEl.addEventListener('scroll', () => {
        if (syncing) return;
        syncing = true;
        graphEl.scrollTop = textEl.scrollTop;
        syncing = false;
    });
}
```

- [ ] **Step 8: 验证编译 + 页面加载**

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "feat(webui): add log-trace.js SVG rendering engine"
```

---

### Task 6: 因果链高亮交互

**Files:**
- Modify: `AgentCoreProcessor/WebUI/wwwroot/js/log-trace.js`

- [ ] **Step 1: 实现 setupInteraction — 因果链高亮**

规则（来自设计文档）：
- 悬停节点 → 只亮直系祖先 + 直系后代子树
- 兄弟分支暗
- close 节点和对应 open 共享高亮集（悬停 close = 悬停 open）
- event 是叶子，只亮祖先链
- 竖线按所属节点 ID 归属独立 dim
- 斜线按 from/to 节点归属 dim

```javascript
function setupInteraction(graphEl, textEl, bodyEl) {
    const { rows, byId, childrenOf, closeToOpen } = state;

    function getAncestors(id) {
        const ancestors = new Set();
        let current = byId[id];
        while (current && current.parentId != null) {
            ancestors.add(current.parentId);
            current = byId[current.parentId];
        }
        return ancestors;
    }

    function getDescendants(id) {
        const desc = new Set();
        const queue = [...(childrenOf[id] || [])];
        while (queue.length > 0) {
            const cid = queue.shift();
            desc.add(cid);
            const children = childrenOf[cid] || [];
            queue.push(...children);
        }
        return desc;
    }

    function getHighlightSet(rowIdx) {
        const row = rows[rowIdx];
        let targetId = row.id;

        // close 节点 → 使用对应 open 的 id
        if (row.type === 'close' && closeToOpen[row.id]) {
            targetId = closeToOpen[row.id];
        }

        const highlighted = new Set([targetId]);
        // 祖先
        for (const a of getAncestors(targetId)) highlighted.add(a);
        // 后代
        for (const d of getDescendants(targetId)) highlighted.add(d);
        // close 本身也亮
        if (row.type === 'close') highlighted.add(row.id);
        // 对应的 open/close 配对
        if (row.type === 'open' && row.spanId) {
            const closeRow = rows.find(r => r.type === 'close' && r.spanId === row.spanId);
            if (closeRow) highlighted.add(closeRow.id);
        }

        return highlighted;
    }

    function applyHighlight(rowIdx) {
        const highlighted = getHighlightSet(rowIdx);
        bodyEl.classList.add('has-hover');

        // Dim graph rows (SVG elements by data-row)
        const allNodes = graphEl.querySelectorAll('.node, .v-line');
        allNodes.forEach(el => {
            const r = parseInt(el.dataset.row);
            const rowData = rows[r];
            if (rowData && !highlighted.has(rowData.id)) {
                el.classList.add('dimmed');
            }
        });

        // Dim cross lines
        graphEl.querySelectorAll('.cross-line').forEach(el => {
            const from = parseInt(el.dataset.from);
            const to = parseInt(el.dataset.to);
            if (!highlighted.has(from) && !highlighted.has(to)) {
                el.classList.add('dimmed');
            }
        });

        // Dim text rows
        textEl.querySelectorAll('.trace-text-row').forEach(el => {
            const r = parseInt(el.dataset.row);
            const rowData = rows[r];
            if (rowData && !highlighted.has(rowData.id)) {
                el.classList.add('dimmed');
            }
        });
    }

    function clearHighlight() {
        bodyEl.classList.remove('has-hover');
        graphEl.querySelectorAll('.dimmed').forEach(el => el.classList.remove('dimmed'));
        textEl.querySelectorAll('.dimmed').forEach(el => el.classList.remove('dimmed'));
    }

    // 事件委托
    graphEl.addEventListener('mouseover', e => {
        const node = e.target.closest('.node');
        if (!node) return;
        const ri = parseInt(node.dataset.row);
        if (!isNaN(ri)) applyHighlight(ri);
    });
    graphEl.addEventListener('mouseout', e => {
        const node = e.target.closest('.node');
        if (node) clearHighlight();
    });
    textEl.addEventListener('mouseover', e => {
        const row = e.target.closest('.trace-text-row');
        if (!row) return;
        const ri = parseInt(row.dataset.row);
        if (!isNaN(ri)) applyHighlight(ri);
    });
    textEl.addEventListener('mouseout', e => {
        const row = e.target.closest('.trace-text-row');
        if (row) clearHighlight();
    });

    state.cleanup = () => {
        // 事件委托在元素上，清空 innerHTML 时自动清理
    };
}
```

- [ ] **Step 2: 验证高亮交互**

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(webui): add causal chain highlight interaction"
```

---

### Task 7: 列头渲染 + 列分隔视觉

**Files:**
- Modify: `AgentCoreProcessor/WebUI/wwwroot/js/log-trace.js`

- [ ] **Step 1: 在 renderGraph 中添加列头**

在 SVG 上方添加一个 HTML 列头行（sticky），显示 scope 名称，宽度与 SVG 列对齐。

```javascript
// 在 renderGraph 开头，graphEl 内先插入列头 div
function renderColHeaders(graphEl) {
    const { colLayout } = state;
    const header = document.createElement('div');
    header.className = 'log-trace-header';
    for (const col of colLayout) {
        const div = document.createElement('div');
        div.className = 'log-trace-col-header';
        div.style.width = col.width + 'px';
        div.textContent = formatScopeName(col.scope);
        header.appendChild(div);
    }
    graphEl.appendChild(header);
}

function formatScopeName(scope) {
    // "channel:qq-123456" → "qq-123456"
    if (scope.startsWith('channel:')) return scope.slice(8);
    if (scope.startsWith('adapter:')) return scope.slice(8);
    return scope;
}
```

- [ ] **Step 2: 列背景交替色**

在 SVG 中为偶数列添加半透明背景矩形：

```javascript
// 在 renderGraph 的列分隔线之前
for (let i = 0; i < colLayout.length; i++) {
    if (i % 2 === 1) {
        const col = colLayout[i];
        const rect = createSvgEl('rect', {
            x: col.x, y: 0, width: col.width, height: totalHeight,
            fill: 'rgba(255,255,255,0.015)', class: 'col-bg'
        });
        svg.appendChild(rect);
    }
}
```

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(webui): add column headers and visual separation"
```

---

### Task 8: 集成测试 + 修复

**Files:**
- All above files

- [ ] **Step 1: 编译运行**

```bash
taskkill /IM AgentCoreProcessor.exe /F 2>/dev/null; dotnet build && dotnet run
```

- [ ] **Step 2: 浏览器验证**

打开 `http://localhost:5000/logs/trace`，验证：
- 页面加载无报错
- 导航栏出现"信号追踪"入口
- 如果有日志数据：SVG 正确渲染列、线、节点
- 如果无数据：显示空状态
- 左右滚动同步
- 悬停高亮工作

- [ ] **Step 3: 修复发现的问题**

- [ ] **Step 4: 最终 Commit**

```bash
git add -A && git commit -m "fix(webui): log trace page integration fixes"
```

---

### Task 9: 筛选功能

**Files:**
- Modify: `AgentCoreProcessor/WebUI/Components/Pages/LogTrace.razor`
- Modify: `AgentCoreProcessor/WebUI/Services/LogTraceService.cs`
- Modify: `AgentCoreProcessor/WebUI/wwwroot/js/log-trace.js`
- Modify: `AgentCoreProcessor/WebUI/wwwroot/css/log-trace.css`

- [ ] **Step 1: 扩展 toolbar — Level 过滤**

在信号选择器旁添加 Level 下拉：
```razor
<select class="form-select form-select-sm" style="width:auto;" @bind="_minLevel" @bind:after="ApplyFilters">
    <option value="0">全部级别</option>
    <option value="1">Info+</option>
    <option value="2">Warn+</option>
    <option value="3">仅 Error</option>
</select>
```

- [ ] **Step 2: Scope 过滤 — 多选 checkbox 下拉**

```razor
<div class="dropdown">
    <button class="btn btn-sm btn-outline-secondary dropdown-toggle" data-bs-toggle="dropdown">
        Scope
    </button>
    <div class="dropdown-menu p-2" style="min-width:180px;">
        @foreach (var scope in _trace?.Scopes ?? new())
        {
            <label class="dropdown-item d-flex align-items-center gap-2" style="cursor:pointer;">
                <input type="checkbox" checked="@IsScopeVisible(scope)"
                       @onchange="e => ToggleScope(scope, (bool)e.Value!)" />
                @FormatScopeName(scope)
            </label>
        }
    </div>
</div>
```

- [ ] **Step 3: "仅未关闭 span" 开关**

```razor
<button class="btn btn-sm @(_showOpenOnly ? "btn-danger" : "btn-outline-secondary")"
        @onclick="ToggleOpenOnly">
    <i class="bi bi-exclamation-circle"></i> 仅卡住
</button>
```

当开启时，LogTraceService 只返回未配对 open 及其祖先链上的事件，方便快速定位问题。

- [ ] **Step 4: LogTraceService 添加过滤参数**

```csharp
public class TraceFilter
{
    public int MinLevel { get; set; } = 0;
    public HashSet<string>? VisibleScopes { get; set; }
    public bool OpenSpansOnly { get; set; }
}

public TraceViewModel GetTrace(string signalId, TraceFilter? filter = null)
{
    var events = _query.GetBySignal(signalId);
    if (filter != null) events = ApplyFilter(events, filter);
    return BuildViewModel(events);
}

private List<LogEvent> ApplyFilter(List<LogEvent> events, TraceFilter filter)
{
    var result = events.Where(e => e.Level >= filter.MinLevel);

    if (filter.VisibleScopes != null)
        result = result.Where(e => filter.VisibleScopes.Contains(e.Scope));

    if (filter.OpenSpansOnly)
    {
        // 找出未配对的 open span
        var closedSpans = events.Where(e => e.Type == "close").Select(e => e.SpanId).ToHashSet();
        var stuckOpens = events.Where(e => e.Type == "open" && !closedSpans.Contains(e.SpanId)).ToList();
        // 保留 stuck open + 其祖先链
        var keepIds = new HashSet<long>();
        foreach (var open in stuckOpens)
        {
            keepIds.Add(open.Id);
            var current = open;
            while (current.ParentId.HasValue)
            {
                keepIds.Add(current.ParentId.Value);
                current = events.FirstOrDefault(e => e.Id == current.ParentId.Value);
                if (current == null) break;
            }
        }
        result = result.Where(e => keepIds.Contains(e.Id));
    }

    return result.ToList();
}
```

- [ ] **Step 5: 前端 ApplyFilters 触发重新渲染**

```csharp
@code {
    private int _minLevel = 0;
    private HashSet<string> _hiddenScopes = new();
    private bool _showOpenOnly = false;

    private async Task ApplyFilters()
    {
        var filter = new TraceFilter
        {
            MinLevel = _minLevel,
            VisibleScopes = _trace?.Scopes.Except(_hiddenScopes).ToHashSet(),
            OpenSpansOnly = _showOpenOnly
        };
        _trace = string.IsNullOrEmpty(_selectedSignal)
            ? TraceService.GetRecentTrace(500, filter)
            : TraceService.GetTrace(_selectedSignal, filter);
        await RenderGraph();
    }
}
```

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(webui): add level/scope/open-only filters to log trace"
```

---

### Task 10: 节点右键快速定位菜单

**Files:**
- Modify: `AgentCoreProcessor/WebUI/wwwroot/js/log-trace.js`
- Modify: `AgentCoreProcessor/WebUI/wwwroot/css/log-trace.css`
- Modify: `AgentCoreProcessor/WebUI/Components/Pages/LogTrace.razor`

- [ ] **Step 1: CSS 上下文菜单样式**

```css
.trace-context-menu {
    position: fixed;
    z-index: 1000;
    background: var(--vis-bg);
    border: 1px solid var(--vis-col-separator);
    border-radius: 6px;
    padding: 4px 0;
    min-width: 160px;
    box-shadow: 0 4px 12px rgba(0,0,0,0.3);
    font-size: 13px;
}
.trace-context-menu .menu-item {
    padding: 6px 12px;
    cursor: pointer;
    color: var(--vis-text);
    display: flex; align-items: center; gap: 6px;
}
.trace-context-menu .menu-item:hover { background: rgba(255,255,255,0.06); }
.trace-context-menu .menu-sep { height: 1px; background: var(--vis-col-separator); margin: 4px 0; }
```

- [ ] **Step 2: JS 右键菜单逻辑**

```javascript
function setupContextMenu(graphEl, textEl, bodyEl) {
    let menu = null;

    function showMenu(e, rowIdx) {
        e.preventDefault();
        hideMenu();
        const row = state.rows[rowIdx];
        menu = document.createElement('div');
        menu.className = 'trace-context-menu';

        // 菜单项
        const items = [];
        items.push({ label: '查看此信号全链', icon: 'bi-diagram-3', action: 'filter-signal', signalId: row.signalId });
        if (row.type === 'open' || row.type === 'close') {
            items.push({ label: '定位配对节点', icon: 'bi-arrow-left-right', action: 'goto-pair', spanId: row.spanId, type: row.type });
        }
        if (row.parentId) {
            items.push({ label: '跳转到父节点', icon: 'bi-arrow-up', action: 'goto-parent', parentId: row.parentId });
        }
        items.push({ sep: true });
        items.push({ label: '复制事件详情', icon: 'bi-clipboard', action: 'copy-detail', detail: row.detail || row.name });

        for (const item of items) {
            if (item.sep) {
                const sep = document.createElement('div');
                sep.className = 'menu-sep';
                menu.appendChild(sep);
            } else {
                const div = document.createElement('div');
                div.className = 'menu-item';
                div.innerHTML = `<i class="bi ${item.icon}"></i>${item.label}`;
                div.addEventListener('click', () => { executeAction(item); hideMenu(); });
                menu.appendChild(div);
            }
        }

        menu.style.left = e.clientX + 'px';
        menu.style.top = e.clientY + 'px';
        document.body.appendChild(menu);
    }

    function hideMenu() {
        if (menu) { menu.remove(); menu = null; }
    }

    function executeAction(item) {
        switch (item.action) {
            case 'filter-signal':
                // 通知 Blazor 切换信号筛选
                DotNet.invokeMethodAsync('AgentCoreProcessor', 'OnTraceAction', 'filter-signal', item.signalId);
                break;
            case 'goto-pair':
                const target = state.rows.find(r =>
                    r.spanId === item.spanId && r.type !== item.type);
                if (target) scrollToRow(state.rows.indexOf(target));
                break;
            case 'goto-parent':
                const parent = state.rows.find(r => r.id == item.parentId);
                if (parent) scrollToRow(state.rows.indexOf(parent));
                break;
            case 'copy-detail':
                navigator.clipboard.writeText(item.detail);
                break;
        }
    }

    function scrollToRow(rowIdx) {
        if (rowIdx < 0) return;
        const y = rowIdx * ROW_HEIGHT;
        graphEl.scrollTop = y - graphEl.clientHeight / 2;
        // 闪烁高亮
        const textRow = textEl.querySelector(`[data-row="${rowIdx}"]`);
        if (textRow) {
            textRow.style.background = 'rgba(96,165,250,0.2)';
            setTimeout(() => textRow.style.background = '', 1500);
        }
    }

    // 绑定右键
    graphEl.addEventListener('contextmenu', e => {
        const node = e.target.closest('.node');
        if (node) showMenu(e, parseInt(node.dataset.row));
    });
    textEl.addEventListener('contextmenu', e => {
        const row = e.target.closest('.trace-text-row');
        if (row) showMenu(e, parseInt(row.dataset.row));
    });
    document.addEventListener('click', hideMenu);
}
```

- [ ] **Step 3: Blazor 端接收 JS 回调**

```csharp
// LogTrace.razor 中添加静态方法供 JS 调用
[JSInvokable]
public static void OnTraceAction(string action, string value)
{
    // 通过静态事件通知实例
    TraceActionReceived?.Invoke(action, value);
}
private static event Action<string, string>? TraceActionReceived;

protected override void OnInitialized()
{
    TraceActionReceived += HandleTraceAction;
}

private async void HandleTraceAction(string action, string value)
{
    if (action == "filter-signal")
    {
        _selectedSignal = value;
        await InvokeAsync(async () => { await LoadTrace(); StateHasChanged(); });
    }
}
```

注意：静态回调方案简单但不支持多实例。如果需要可改为 DotNetObjectReference 实例回调。对于管理面板单用户场景足够。

- [ ] **Step 4: 验证右键菜单功能**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(webui): add context menu with quick navigation for trace nodes"
```

---

### Task 11: 实时推送 + 自动滚动

**Files:**
- Modify: `AgentCoreProcessor/WebUI/Services/LogTraceService.cs`
- Modify: `AgentCoreProcessor/WebUI/Components/Pages/LogTrace.razor`
- Modify: `AgentCoreProcessor/WebUI/wwwroot/js/log-trace.js`

- [ ] **Step 1: LogTraceService 订阅实时事件**

LogWriter 已有订阅机制（`LogAccessImpl` 的 `Subscribe`）。LogTraceService 暴露订阅接口：

```csharp
internal class LogTraceService : IDisposable
{
    private readonly ILogQuery _query;
    private readonly ILogAccess _logAccess;

    public LogTraceService(ILogQuery query, ILogAccess logAccess)
    {
        _query = query;
        _logAccess = logAccess;
    }

    public IDisposable Subscribe(Action<TraceRow> onNewRow)
    {
        return _logAccess.Subscribe(evt =>
        {
            var row = new TraceRow
            {
                Id = evt.Id, SignalId = evt.SignalId, Scope = evt.Scope,
                ParentId = evt.ParentId, SpanId = evt.SpanId, Type = evt.Type,
                Level = evt.Level, Timestamp = evt.Timestamp,
                Name = evt.Name, Detail = evt.Detail, GroupName = evt.GroupName
            };
            onNewRow(row);
        });
    }
}
```

- [ ] **Step 2: Razor 端实时接收 + 增量推送到 JS**

```csharp
@code {
    private IDisposable? _subscription;
    private bool _autoScroll = true;
    private bool _paused = false;

    protected override void OnInitialized()
    {
        // ...existing code...
        _subscription = TraceService.Subscribe(OnNewEvent);
    }

    private void OnNewEvent(TraceRow row)
    {
        if (_paused) return;
        // 筛选检查
        if (row.Level < _minLevel) return;
        if (_hiddenScopes.Contains(row.Scope)) return;
        if (!string.IsNullOrEmpty(_selectedSignal) && row.SignalId != _selectedSignal) return;

        InvokeAsync(async () =>
        {
            if (_jsModule != null)
                await _jsModule.InvokeVoidAsync("appendRow", row, _autoScroll);
        });
    }
}
```

- [ ] **Step 3: JS appendRow — 增量追加**

```javascript
export function appendRow(row, autoScroll) {
    if (!state) return;
    state.rows.push(row);

    // 如果超过最大条数，移除顶部
    if (state.rows.length > state.maxRows) {
        const removeCount = state.rows.length - state.maxRows;
        state.rows.splice(0, removeCount);
        // 需要完整重绘（因为行索引全变了）
        fullRedraw();
        return;
    }

    // 增量：计算新行的 slot 状态，追加 SVG 元素 + 文本行
    const ri = state.rows.length - 1;
    computeRowSlot(row, ri);
    appendSvgRow(ri);
    appendTextRow(ri);

    // 更新 SVG 高度
    state.svg.setAttribute('height', state.rows.length * ROW_HEIGHT);

    if (autoScroll) {
        state.graphEl.scrollTop = state.graphEl.scrollHeight;
        state.textEl.scrollTop = state.textEl.scrollHeight;
    }
}
```

- [ ] **Step 4: 自动滚动 + 暂停按钮**

```razor
<button class="btn btn-sm @(_paused ? "btn-warning" : "btn-outline-secondary")" @onclick="TogglePause">
    <i class="bi @(_paused ? "bi-play-fill" : "bi-pause-fill")"></i>
    @(_paused ? "继续" : "暂停")
</button>
<button class="btn btn-sm @(_autoScroll ? "btn-info" : "btn-outline-secondary")" @onclick="() => _autoScroll = !_autoScroll">
    <i class="bi bi-arrow-down-circle"></i> 自动滚动
</button>
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(webui): add real-time event streaming and auto-scroll"
```

---

### Task 12: 虚拟化渲染 + 懒加载

**Files:**
- Modify: `AgentCoreProcessor/WebUI/wwwroot/js/log-trace.js`
- Modify: `AgentCoreProcessor/WebUI/Services/LogTraceService.cs`
- Modify: `AgentCoreProcessor/WebUI/Components/Pages/LogTrace.razor`

核心问题：SVG 节点数过多（数千行 × 每行多个元素）会卡。解决方案：只渲染可视区域 ± 缓冲区的 SVG 元素，滚动时动态替换。

- [ ] **Step 1: 虚拟化渲染策略**

```javascript
const BUFFER_ROWS = 30; // 可视区上下各多渲染 30 行
const MAX_RENDER_ROWS = 2000; // 内存中最多保留的数据行数

// 渲染时不画全部行，只画可视窗口
function renderVisibleRange(graphEl, textEl) {
    const scrollTop = graphEl.scrollTop;
    const viewHeight = graphEl.clientHeight;
    const firstVisible = Math.floor(scrollTop / ROW_HEIGHT);
    const lastVisible = Math.ceil((scrollTop + viewHeight) / ROW_HEIGHT);
    const renderStart = Math.max(0, firstVisible - BUFFER_ROWS);
    const renderEnd = Math.min(state.rows.length, lastVisible + BUFFER_ROWS);

    if (renderStart === state.renderStart && renderEnd === state.renderEnd) return;
    state.renderStart = renderStart;
    state.renderEnd = renderEnd;

    // 清除旧的行元素，重绘可视范围
    clearRenderedRows();
    for (let ri = renderStart; ri < renderEnd; ri++) {
        renderSvgRow(ri);
        renderTextRowEl(ri);
    }

    // 斜线只画涉及可视范围的
    renderVisibleCrossLines(renderStart, renderEnd);
}

// SVG 总高度保持 rows.length * ROW_HEIGHT（撑开滚动条）
// 用一个透明 spacer rect 撑高度，实际元素只在可视区
```

- [ ] **Step 2: 滚动事件节流 + 重绘**

```javascript
function setupVirtualScroll(graphEl, textEl) {
    let rafId = null;
    const onScroll = () => {
        if (rafId) return;
        rafId = requestAnimationFrame(() => {
            renderVisibleRange(graphEl, textEl);
            rafId = null;
        });
    };
    graphEl.addEventListener('scroll', onScroll);
    textEl.addEventListener('scroll', onScroll);
}
```

- [ ] **Step 3: 向上懒加载旧日志**

当用户滚动到顶部时，请求更早的日志并拼接到前面：

```javascript
function setupLazyLoadUp(graphEl) {
    graphEl.addEventListener('scroll', () => {
        if (graphEl.scrollTop < ROW_HEIGHT * 5 && !state.loadingOlder && state.hasOlder) {
            state.loadingOlder = true;
            // 通知 Blazor 加载更早数据
            DotNet.invokeMethodAsync('AgentCoreProcessor', 'OnLoadOlder', state.rows[0]?.timestamp);
        }
    });
}

export function prependRows(olderRows, hasMore) {
    if (!state) return;
    const prevHeight = state.rows.length * ROW_HEIGHT;
    const prevScrollTop = state.graphEl.scrollTop;

    state.rows.unshift(...olderRows);
    state.hasOlder = hasMore;
    state.loadingOlder = false;

    // 重算所有 slot 状态（因为行索引全变了）
    recomputeAllSlots();

    // 更新 SVG 高度
    const newHeight = state.rows.length * ROW_HEIGHT;
    state.svg.setAttribute('height', newHeight);

    // 保持滚动位置（补偿新增高度）
    const addedHeight = olderRows.length * ROW_HEIGHT;
    state.graphEl.scrollTop = prevScrollTop + addedHeight;
    state.textEl.scrollTop = prevScrollTop + addedHeight;

    renderVisibleRange(state.graphEl, state.textEl);
}
```

- [ ] **Step 4: LogTraceService 分页查询**

```csharp
public TraceViewModel GetTraceBefore(string signalId, long beforeTimestamp, int limit = 200)
{
    // 获取指定信号中 timestamp < beforeTimestamp 的事件
    var events = _query.GetBySignal(signalId)
        .Where(e => e.Timestamp < beforeTimestamp)
        .OrderByDescending(e => e.Timestamp)
        .Take(limit)
        .Reverse()
        .ToList();
    return BuildViewModel(events);
}

public TraceViewModel GetRecentBefore(long beforeTimestamp, int limit = 200, TraceFilter? filter = null)
{
    // 需要在 ILogQuery 中添加 before 参数支持，或直接用 GetRecent + 过滤
    var events = _query.GetRecent(limit + 200) // 多取一些再过滤
        .Where(e => e.Timestamp < beforeTimestamp)
        .Take(limit)
        .ToList();
    if (filter != null) events = ApplyFilter(events, filter);
    return BuildViewModel(events);
}
```

- [ ] **Step 5: Blazor 端 LoadOlder 回调**

```csharp
[JSInvokable]
public static void OnLoadOlder(long beforeTimestamp)
{
    LoadOlderRequested?.Invoke(beforeTimestamp);
}
private static event Action<long>? LoadOlderRequested;

private async void HandleLoadOlder(long beforeTimestamp)
{
    var older = string.IsNullOrEmpty(_selectedSignal)
        ? TraceService.GetRecentBefore(beforeTimestamp, 200, BuildFilter())
        : TraceService.GetTraceBefore(_selectedSignal, beforeTimestamp, 200);

    if (_jsModule != null && older.Rows.Count > 0)
    {
        await InvokeAsync(async () =>
        {
            await _jsModule.InvokeVoidAsync("prependRows", older.Rows, older.Rows.Count >= 200);
        });
    }
}
```

- [ ] **Step 6: 最大条数限制**

JS 端 `MAX_RENDER_ROWS = 2000`，超过时从顶部丢弃（实时模式）或禁止继续加载（历史模式）。

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(webui): add virtual rendering and lazy-load for log trace"
```

---

## 实现注意事项

1. **所有线条统一 SVG**：竖线和斜线全部在同一个 SVG 元素中渲染，每段线标记 `data-row` 和 `data-signal`，支持独立 dim。不使用 CSS div 画线（预览中的临时方案）。

2. **列宽统一 ≥ 180px**：不按内容动态缩放，避免视觉跳动。

3. **行高对齐**：左侧 SVG 和右侧文本都使用 `ROW_HEIGHT = 26px`，确保同步滚动时行对齐。

4. **Provider 注册**：LogTraceProvider 只负责导航入口，页面渲染由独立的 `@page "/logs/trace"` 路由处理。同时注册 `/p/logs/trace` 确保 NavMenu 生成的链接能匹配。

5. **数据来源**：通过 `ILogQuery.GetBySignal()` 和 `GetSignalList()` 获取，不需要新增数据库查询。
