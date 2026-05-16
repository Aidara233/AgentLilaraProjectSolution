// log-trace.js — 信号追踪 SVG 渲染引擎
// Blazor 通过 IJSRuntime 导入此 ES module

const ROW_HEIGHT = 26;
const SLOT_WIDTH = 14;
const NODE_SIZE = 9;
const LINE_WIDTH = 1.5;
const CROSS_LINE_WIDTH = 2.5;
const COL_PADDING = 12;
const MIN_COL_WIDTH = 180;

const VIRTUAL_BUFFER = 30; // rows above/below viewport to pre-render
const MAX_ROWS = 2000;     // max rows to keep in memory

const SVG_NS = 'http://www.w3.org/2000/svg';

// Module-level state
let state = null;

function initState() {
    return {
        graphEl: null,
        textEl: null,
        bodyEl: null,
        detailEl: null,
        scopes: [],
        rows: [],
        columns: [],      // { scope, slotCount, width, x }
        rowMeta: [],      // per-row: { _slotIdx, _scopeIdx, _x, _y }
        svgEl: null,
        headerEl: null,
        scrollSyncing: false,
        byId: {},         // id → row object
        childrenOf: {},   // parentId → [child ids]
        closeToOpen: {},  // close row id → paired open row id (matched by spanId)
        causeSpanIdToRowId: {}, // effectRowId → causeRowId (cross-scope causation)
        effectsOf: {},       // causeRowId → [effectRowIds] (reverse causal map)
        _renderStart: -1, // virtual render range start
        _renderEnd: -1,   // virtual render range end
        _contentGroup: null, // SVG <g> for dynamic content
        _textContainer: null, // div container for text rows
        _rafPending: false,  // rAF throttle flag
        _spans: null,        // pre-computed span map for vertical lines
        engineLifecycles: [], // engine start/stop pairs
        startupSignalClosed: false, // whether system:init has a close event
        ceaseIndices: [], // row indices of cease events (process interruption markers)
        hoverPaused: false,        // auto-scroll paused due to hover
        _lastHighlightRowId: -1,   // restore highlight after rebuildState
        _resumeTimer: null         // timer to resume auto-scroll after hover
    };
}

// --- Build lookup maps for interaction ---

function buildLookupMaps(rows) {
    const byId = {};
    const childrenOf = {};
    const closeToOpen = {};

    // Index rows by id and by spanId
    const idBySpanId = {};
    for (const row of rows) {
        byId[row.id] = row;
        if (row.type === 'open' && row.spanId != null) {
            idBySpanId[row.spanId] = row.id;
        }
    }

    // Build childrenOf: parent_id is now same-scope nesting only
    for (const row of rows) {
        row._resolvedParentId = null;
        if (row.parentId != null) {
            const parentRowId = idBySpanId[row.parentId] ?? null;
            if (parentRowId != null) {
                row._resolvedParentId = parentRowId;
                if (!childrenOf[parentRowId]) childrenOf[parentRowId] = [];
                childrenOf[parentRowId].push(row.id);
            }
        }
    }

    // Build causeSpanIdToRowId: cross-scope causation lookup
    const causeSpanIdToRowId = {};
    for (const row of rows) {
        if (row.causeSpanId != null && idBySpanId[row.causeSpanId] != null) {
            causeSpanIdToRowId[row.id] = idBySpanId[row.causeSpanId];
        }
    }

    // Build engine lifecycle ranges: pair open/close of "*引擎*" spans by scope
    const engineLifecycles = [];
    for (let i = 0; i < rows.length; i++) {
        const row = rows[i];
        if (row.type === 'open' && row.name && row.name.includes('引擎')) {
            // Find matching close
            let endIdx = -1;
            for (let j = i + 1; j < rows.length; j++) {
                if (rows[j].type === 'close' && rows[j].spanId === row.spanId) {
                    endIdx = j;
                    break;
                }
            }
            let engineType = null;
            try { engineType = JSON.parse(row.detail || '{}').engineType; } catch {}
            engineLifecycles.push({ engineType, scope: row.scope, startIdx: i, endIdx });
        }
    }

    // Detect if startupSignal has a close event (scope=system:init)
    let startupSignalClosed = false;
    for (const row of rows) {
        if (row.scope === 'system:init' && row.type === 'close') {
            startupSignalClosed = true;
            break;
        }
    }

    // Detect cease events (process interruption markers)
    const ceaseIndices = [];
    for (let i = 0; i < rows.length; i++) {
        if (rows[i].type === 'cease') ceaseIndices.push(i);
    }

    // Build closeToOpen: match close rows to their paired open rows by spanId
    const openBySpanId = {};
    for (const row of rows) {
        if (row.type === 'open' && row.spanId != null) {
            openBySpanId[row.spanId] = row.id;
        }
    }
    for (const row of rows) {
        if (row.type === 'close' && row.spanId != null && openBySpanId[row.spanId] != null) {
            closeToOpen[row.id] = openBySpanId[row.spanId];
        }
    }

    return { byId, childrenOf, closeToOpen, causeSpanIdToRowId, engineLifecycles, startupSignalClosed, ceaseIndices };
}

// --- Scope ordering ---

const SCOPE_PRIORITY = [
    'system:init',
    'system:',
    'timer:',
    'dream:',
    'vision:',
    'review:',
    'adapter:',
    'channel:'
];

function scopePriority(scope) {
    for (let i = 0; i < SCOPE_PRIORITY.length; i++) {
        if (scope === SCOPE_PRIORITY[i] || scope.startsWith(SCOPE_PRIORITY[i])) return i;
    }
    return SCOPE_PRIORITY.length;
}

function sortScopes(scopes) {
    return [...scopes].sort((a, b) => {
        const pa = scopePriority(a), pb = scopePriority(b);
        if (pa !== pb) return pa - pb;
        return a.localeCompare(b);
    });
}

// --- Slot assignment ---

function assignSlots(rows, scopes) {
    // Track active spans per scope: Map<scopeIdx, Map<spanId, slotIdx>>
    const activePerScope = new Map();
    // Track max slot count per scope
    const maxSlots = new Array(scopes.length).fill(0);

    for (const scope of scopes) {
        activePerScope.set(scopes.indexOf(scope), new Map());
    }

    const meta = [];

    for (let i = 0; i < rows.length; i++) {
        const row = rows[i];
        const scopeIdx = scopes.indexOf(row.scope);
        const active = activePerScope.get(scopeIdx) || new Map();

        let slotIdx = 0;

        if (row.type === 'cease' || (row.type === 'open' && row.name === '程序启动')) {
            // Process restart: release all active slots across all scopes
            for (const [, scopeActive] of activePerScope) {
                scopeActive.clear();
            }
            if (row.type === 'open') {
                active.set(row.spanId, 0);
                if (1 > maxSlots[scopeIdx]) maxSlots[scopeIdx] = 1;
            }
            slotIdx = 0;
        } else if (row.type === 'open') {
            // Find first unused slot index
            const usedSlots = new Set(active.values());
            slotIdx = 0;
            while (usedSlots.has(slotIdx) && slotIdx < 5) slotIdx++;
            active.set(row.spanId, slotIdx);
            if (slotIdx + 1 > maxSlots[scopeIdx]) maxSlots[scopeIdx] = slotIdx + 1;
        } else if (row.type === 'close') {
            // Release the slot matching this spanId
            if (active.has(row.spanId)) {
                slotIdx = active.get(row.spanId);
                active.delete(row.spanId);
            }
        } else {
            // event: use deepest active slot
            if (active.size > 0) {
                slotIdx = Math.max(...active.values());
            }
        }

        meta.push({ _slotIdx: slotIdx, _scopeIdx: scopeIdx });
    }

    // Ensure at least 1 slot per scope
    for (let i = 0; i < maxSlots.length; i++) {
        if (maxSlots[i] < 1) maxSlots[i] = 1;
    }

    return { meta, maxSlots };
}

// --- Column layout ---

function computeColumns(scopes, maxSlots) {
    const columns = [];
    let x = 0;
    for (let i = 0; i < scopes.length; i++) {
        const slotCount = maxSlots[i];
        const width = Math.max(MIN_COL_WIDTH, slotCount * SLOT_WIDTH + COL_PADDING * 2);
        columns.push({ scope: scopes[i], slotCount, width, x });
        x += width;
    }
    return columns;
}

function getNodeX(columns, scopeIdx, slotIdx) {
    const col = columns[scopeIdx];
    // Center slots within column
    const totalSlotsWidth = col.slotCount * SLOT_WIDTH;
    const startX = col.x + COL_PADDING + (col.width - COL_PADDING * 2 - totalSlotsWidth) / 2;
    return startX + slotIdx * SLOT_WIDTH + SLOT_WIDTH / 2;
}

function getNodeY(rowIdx) {
    return rowIdx * ROW_HEIGHT + ROW_HEIGHT / 2;
}

// --- SVG helpers ---

function svgEl(tag, attrs) {
    const el = document.createElementNS(SVG_NS, tag);
    if (attrs) {
        for (const [k, v] of Object.entries(attrs)) {
            el.setAttribute(k, v);
        }
    }
    return el;
}

// --- Render column headers (HTML) ---

function formatScopeName(scope) {
    const map = {
        'system:init': '系统启动',
        'system:main': '系统循环',
        'timer:heartbeat': 'Timer心跳',
        'dream:main': '做梦',
        'vision:main': '视觉',
        'review:main': '回顾',
    };
    if (map[scope]) return map[scope];
    if (scope.startsWith('channel:')) return '频道 ' + scope.substring(8);
    if (scope.startsWith('adapter:')) return '适配器 ' + scope.substring(8);
    if (scope.startsWith('dream:')) return '做梦 ' + scope.substring(6);
    return scope;
}

function renderHeader(graphEl, columns) {
    let header = graphEl.querySelector('.log-trace-header');
    if (!header) {
        header = document.createElement('div');
        header.className = 'log-trace-header';
        graphEl.prepend(header);
    }
    header.innerHTML = '';

    for (const col of columns) {
        const div = document.createElement('div');
        div.className = 'log-trace-col-header';
        div.style.width = col.width + 'px';
        div.textContent = formatScopeName(col.scope);
        header.appendChild(div);
    }

    state.headerEl = header;
}

// --- Render SVG (shell only — content rendered by renderVisibleRange) ---

function renderSvg(graphEl, columns, rows, meta) {
    // Remove old SVG
    const oldSvg = graphEl.querySelector('svg');
    if (oldSvg) oldSvg.remove();

    const totalWidth = columns.reduce((sum, c) => sum + c.width, 0);
    const totalHeight = rows.length * ROW_HEIGHT;

    const svg = svgEl('svg', {
        width: totalWidth,
        height: totalHeight,
        class: 'log-trace-svg'
    });
    svg.style.display = 'block';

    // Spacer rect to maintain full scroll height
    svg.appendChild(svgEl('rect', {
        x: 0, y: 0, width: totalWidth, height: totalHeight,
        fill: 'transparent', class: 'spacer'
    }));

    // Column backgrounds and separators (static, always rendered)
    for (let i = 0; i < columns.length; i++) {
        const col = columns[i];
        if (i % 2 === 1) {
            svg.appendChild(svgEl('rect', {
                x: col.x, y: 0, width: col.width, height: totalHeight,
                fill: 'rgba(255,255,255,0.015)', class: 'col-bg'
            }));
        }
        if (i < columns.length - 1) {
            svg.appendChild(svgEl('line', {
                x1: col.x + col.width, y1: 0,
                x2: col.x + col.width, y2: totalHeight,
                stroke: 'var(--vis-col-separator)',
                'stroke-width': 1,
                'stroke-dasharray': '3,3',
                class: 'col-sep'
            }));
        }
    }

    // Content group for dynamic elements (cleared on each virtual render)
    const contentGroup = svgEl('g', { class: 'content' });
    svg.appendChild(contentGroup);
    state._contentGroup = contentGroup;

    // Pre-compute span map for vertical lines
    state._spans = buildSpanMap(rows, meta);

    graphEl.appendChild(svg);
    state.svgEl = svg;
}

// --- Build span map (pre-computed once, used by renderVerticalLinesRange) ---

function buildSpanMap(rows, meta) {
    const spans = new Map();

    // First pass: create span entries
    for (let i = 0; i < rows.length; i++) {
        const row = rows[i];
        const m = meta[i];
        if (row.type === 'open') {
            spans.set(row.spanId, {
                openIdx: i,
                closeIdx: -1,
                scopeIdx: m._scopeIdx,
                slotIdx: m._slotIdx,
                openRowId: row.id,
                childSpanIds: [],
                lastDescendantRow: i
            });
        } else if (row.type === 'close') {
            const span = spans.get(row.spanId);
            if (span) span.closeIdx = i;
        }
    }

    // Second pass: build child relationships (same-scope parent_id only)
    for (let i = 0; i < rows.length; i++) {
        const row = rows[i];
        if (row.type === 'open' && row.parentId != null) {
            const parentSpan = spans.get(row.parentId);
            if (parentSpan) {
                parentSpan.childSpanIds.push(row.spanId);
            }
        }
    }

    // Third pass: compute lastDescendantRow bottom-up (post-order)
    function computeLastDesc(spanId) {
        const span = spans.get(spanId);
        if (span._lastComputed) return span.lastDescendantRow;
        span._lastComputed = true;

        let maxRow = span.closeIdx >= 0 ? span.closeIdx : span.openIdx;
        for (const childId of span.childSpanIds) {
            maxRow = Math.max(maxRow, computeLastDesc(childId));
        }
        span.lastDescendantRow = maxRow;
        return maxRow;
    }

    for (const [spanId, span] of spans) {
        computeLastDesc(spanId);
    }

    // Fourth pass: extend lastDescendantRow for events parented under spans
    // (non-span events still extend the visual range of their parent span)
    for (let i = 0; i < rows.length; i++) {
        const row = rows[i];
        if (row.parentId != null && row.type !== 'open' && row.type !== 'close') {
            const parentSpan = spans.get(row.parentId);
            if (parentSpan && parentSpan.lastDescendantRow < i) {
                parentSpan.lastDescendantRow = i;
                // Propagate upward: any ancestor span should also extend
                let current = parentSpan;
                while (current) {
                    if (current.lastDescendantRow < i) current.lastDescendantRow = i;
                    const grandparentId = rows[current.openIdx].parentId;
                    current = grandparentId ? spans.get(grandparentId) : null;
                }
            }
        }
    }

    // Fifth pass: cap unclosed spans at the nearest cease event after their open
    const ceaseIndices = state.ceaseIndices || [];
    if (ceaseIndices.length > 0) {
        for (const [spanId, span] of spans) {
            if (span.closeIdx >= 0) continue; // already closed
            // Find first cease event after this span's open
            for (const ci of ceaseIndices) {
                if (ci > span.openIdx) {
                    span.lastDescendantRow = Math.min(span.lastDescendantRow, ci);
                    span._interrupted = true;
                    break;
                }
            }
        }
    }

    return spans;
}

function renderVerticalLinesRange(g, columns, start, end) {
    const spans = state._spans;
    if (!spans) return;

    for (const [spanId, span] of spans) {
        const { openIdx, closeIdx, scopeIdx, slotIdx, openRowId, lastDescendantRow } = span;
        const x = getNodeX(columns, scopeIdx, slotIdx);
        // End at close row; if no close, end at deepest descendant (tree-based)
        const endIdx = lastDescendantRow;

        // Skip spans where end is before open (degenerate: orphan open with no descendants)
        if (endIdx <= openIdx && closeIdx < 0) continue;

        // Skip spans that don't overlap with [start, end)
        if (endIdx < start || openIdx >= end) continue;

        const segStart = Math.max(openIdx, start);
        const segEnd = Math.min(endIdx, end - 1);

        for (let r = segStart; r <= segEnd; r++) {
            let y1, y2;
            if (r === openIdx) {
                y1 = getNodeY(r);
                y2 = r * ROW_HEIGHT + ROW_HEIGHT;
            } else if (r === endIdx && closeIdx >= 0) {
                y1 = r * ROW_HEIGHT;
                y2 = getNodeY(r);
            } else {
                y1 = r * ROW_HEIGHT;
                y2 = r * ROW_HEIGHT + ROW_HEIGHT;
            }

            const line = svgEl('line', {
                x1: x, y1: y1, x2: x, y2: y2,
                stroke: 'var(--vis-line)',
                'stroke-width': LINE_WIDTH,
                class: 'v-line',
                'data-row': r,
                'data-id': openRowId
            });
            g.appendChild(line);
        }
    }
}

// --- Render cross-scope diagonal lines (range only) ---

function renderCrossLinesRange(g, rows, meta, columns, start, end) {
    const { causeSpanIdToRowId, _spans } = state;
    if (!causeSpanIdToRowId) return;

    // Build row index by id for quick lookup
    const rowById = new Map();
    for (let i = 0; i < rows.length; i++) {
        rowById.set(rows[i].id, i);
    }

    for (let i = start; i < end; i++) {
        const row = rows[i];
        const causeRowId = causeSpanIdToRowId[row.id];
        if (causeRowId == null) continue;

        const causeIdx = rowById.get(causeRowId);
        if (causeIdx == null) continue;

        const causeMeta = meta[causeIdx];
        const effectMeta = meta[i];

        // cause_span_id always means cross-scope by definition
        if (causeMeta._scopeIdx === effectMeta._scopeIdx) continue;

        // From: cause span's open (signal source), not close
        const fromIdx = causeIdx;
        const fromMeta = meta[fromIdx];

        const x1 = getNodeX(columns, fromMeta._scopeIdx, fromMeta._slotIdx);
        const y1 = getNodeY(fromIdx);
        const x2 = getNodeX(columns, effectMeta._scopeIdx, effectMeta._slotIdx);
        const y2 = getNodeY(i);

        const line = svgEl('line', {
            x1, y1, x2, y2,
            stroke: 'var(--vis-line-cross)',
            'stroke-width': CROSS_LINE_WIDTH,
            class: 'cross-line',
            'data-from': rows[causeIdx].id,
            'data-to': row.id
        });
        g.appendChild(line);
    }

    // Also render lines where the CAUSE row is visible but effect row is outside range
    for (const [effectRowId, causeRowId] of Object.entries(causeSpanIdToRowId)) {
        const effectIdx = rowById.get(parseInt(effectRowId));
        if (effectIdx == null || (effectIdx >= start && effectIdx < end)) continue; // already handled

        const causeIdx = rowById.get(causeRowId);
        if (causeIdx == null || causeIdx < start || causeIdx >= end) continue;

        const row = rows[effectIdx];
        const causeMeta = meta[causeIdx];
        const effectMeta = meta[effectIdx];
        if (causeMeta._scopeIdx === effectMeta._scopeIdx) continue;

        const fromIdx = causeIdx;
        const fromMeta = meta[fromIdx];

        const x1 = getNodeX(columns, fromMeta._scopeIdx, fromMeta._slotIdx);
        const y1 = getNodeY(fromIdx);
        const x2 = getNodeX(columns, effectMeta._scopeIdx, effectMeta._slotIdx);
        const y2 = getNodeY(effectIdx);

        const line = svgEl('line', {
            x1, y1, x2, y2,
            stroke: 'var(--vis-line-cross)',
            'stroke-width': CROSS_LINE_WIDTH,
            class: 'cross-line',
            'data-from': rows[causeIdx].id,
            'data-to': row.id
        });
        g.appendChild(line);
    }
}

// --- Render engine lifecycle lines (gray dashed, annotation only) ---

function renderEngineLifecycleLines(g, rows, meta, columns, start, end) {
    const lifecycles = state.engineLifecycles;
    if (!lifecycles || lifecycles.length === 0) return;
    const ceaseIndices = state.ceaseIndices || [];

    for (const lc of lifecycles) {
        const startIdx = lc.startIdx;
        let endIdx = lc.endIdx >= 0 ? lc.endIdx : rows.length - 1;

        // Cap at cease event if unclosed
        if (lc.endIdx < 0 && ceaseIndices.length > 0) {
            for (const ci of ceaseIndices) {
                if (ci > startIdx) { endIdx = ci; break; }
            }
        }

        // Skip if entirely outside visible range
        if (endIdx < start || startIdx >= end) continue;

        const segStart = Math.max(startIdx, start);
        const segEnd = Math.min(endIdx, end - 1);

        // Use the scope column and slot 0 (leftmost) offset by -6px for the annotation line
        const scopeIdx = meta[startIdx]._scopeIdx;
        const x = getNodeX(columns, scopeIdx, 0) - 6;

        const y1 = getNodeY(segStart) - (segStart === startIdx ? 0 : ROW_HEIGHT / 2);
        const y2 = getNodeY(segEnd) + (segEnd === endIdx && lc.endIdx >= 0 ? 0 : ROW_HEIGHT / 2);

        const line = svgEl('line', {
            x1: x, y1, x2: x, y2,
            stroke: 'var(--vis-debug)',
            'stroke-width': 1,
            'stroke-dasharray': '4,4',
            opacity: '0.6',
            class: 'engine-life'
        });
        g.appendChild(line);
    }
}

// --- Render nodes (range only) ---

function renderNodesRange(g, rows, meta, columns, start, end) {
    // Build span close set
    const spanHasClose = new Set();
    for (const row of rows) {
        if (row.type === 'close') spanHasClose.add(row.spanId);
    }

    // Build set of spanIds that are causal sources (referenced by other rows' causeSpanId)
    const isCausalSource = new Set();
    for (const row of rows) {
        if (row.causeSpanId != null) isCausalSource.add(row.causeSpanId);
    }

    // Color for unclosed spans: green/yellow/red/gray
    function getOpenColor(spanId, row) {
        if (spanHasClose.has(spanId)) return 'var(--vis-ok)';
        // Check if interrupted by cease event
        const span = state._spans ? state._spans.get(spanId) : null;
        if (span && span._interrupted) return 'var(--vis-debug)'; // gray = interrupted
        // No close — check parent chain
        if (row.parentId != null) {
            if (spanHasClose.has(row.parentId)) return 'var(--vis-error)';
            return 'var(--vis-warn)';
        }
        if (state.startupSignalClosed) return 'var(--vis-error)';
        return 'var(--vis-warn)';
    }

    function isSignalOrigin(row) {
        if (row.parentId == null && row.causeSpanId == null) return true;
        if (row.spanId != null && isCausalSource.has(row.spanId)) return true;
        return false;
    }

    for (let i = start; i < end; i++) {
        const row = rows[i];
        const m = meta[i];
        const cx = getNodeX(columns, m._scopeIdx, m._slotIdx);
        const cy = getNodeY(i);
        const r = NODE_SIZE / 2;

        let node;

        if (row.type === 'open' && isSignalOrigin(row)) {
            // Signal origin: double circle
            const originColor = getOpenColor(row.spanId, row);
            const g2 = svgEl('g', { class: 'node', 'data-row': i, 'data-id': row.id });
            g2.appendChild(svgEl('circle', {
                cx, cy, r: r + 2,
                fill: 'none', stroke: originColor, 'stroke-width': 1.5
            }));
            g2.appendChild(svgEl('circle', {
                cx, cy, r: r - 1,
                fill: 'none', stroke: originColor, 'stroke-width': 1.5
            }));
            node = g2;
        } else if (row.type === 'open') {
            const color = getOpenColor(row.spanId, row);
            node = svgEl('circle', {
                cx, cy, r,
                fill: 'none', stroke: color, 'stroke-width': 1.5,
                class: 'node', 'data-row': i, 'data-id': row.id
            });
        } else if (row.type === 'close') {
            node = svgEl('circle', {
                cx, cy, r,
                fill: 'none', stroke: 'var(--vis-ok)', 'stroke-width': 1.5,
                class: 'node', 'data-row': i, 'data-id': row.id
            });
        } else {
            let color;
            if (row.level >= 3) color = 'var(--vis-error)';
            else if (row.level === 2) color = 'var(--vis-warn)';
            else if (row.level === 0) color = 'var(--vis-debug)';
            else color = 'var(--vis-info)';
            node = svgEl('circle', {
                cx, cy, r: r - 1,
                fill: color, stroke: 'none',
                class: 'node', 'data-row': i, 'data-id': row.id
            });
        }

        g.appendChild(node);
    }
}

// --- Render text rows (shell only — content rendered by renderVisibleRange) ---

function renderTextRows(textEl, rows) {
    textEl.innerHTML = '';

    // Add a header spacer to align with the graph column header
    const headerSpacer = document.createElement('div');
    headerSpacer.className = 'log-trace-header';
    headerSpacer.style.borderBottom = '1px solid var(--vis-col-separator)';
    const timeLabel = document.createElement('span');
    timeLabel.style.cssText = 'width:70px;flex-shrink:0;font-size:12px;color:var(--vis-text-dim);';
    timeLabel.textContent = '时间';
    const scopeLabel = document.createElement('span');
    scopeLabel.style.cssText = 'width:90px;flex-shrink:0;font-size:12px;color:var(--vis-text-dim);padding-left:0.5rem;';
    scopeLabel.textContent = '来源';
    const nameLabel = document.createElement('span');
    nameLabel.style.cssText = 'flex:1;font-size:12px;color:var(--vis-text-dim);padding-left:0.5rem;';
    nameLabel.textContent = '事件';
    headerSpacer.appendChild(timeLabel);
    headerSpacer.appendChild(scopeLabel);
    headerSpacer.appendChild(nameLabel);
    textEl.appendChild(headerSpacer);

    // Container for virtual text rows (with padding-top offset)
    const container = document.createElement('div');
    container.className = 'trace-text-container';
    container.style.height = (rows.length * ROW_HEIGHT) + 'px';
    container.style.position = 'relative';
    textEl.appendChild(container);
    state._textContainer = container;
}

// --- Render text rows for a range ---

function textScopeName(scope) {
    const map = {
        'system:init': '系统启动',
        'system:main': '系统循环',
        'timer:heartbeat': 'Timer',
        'dream:main': '做梦',
        'vision:main': '视觉',
        'review:main': '回顾',
    };
    if (map[scope]) return map[scope];
    if (scope.startsWith('channel:')) return '频道 ' + scope.substring(8);
    if (scope.startsWith('adapter:')) return '适配器';
    if (scope.startsWith('dream:')) return '做梦';
    return scope;
}

function renderTextRange(start, end) {
    const container = state._textContainer;
    if (!container) return;
    const { rows } = state;

    // Clear existing rows
    container.innerHTML = '';

    // Offset spacer
    container.style.paddingTop = (start * ROW_HEIGHT) + 'px';
    container.style.height = (rows.length * ROW_HEIGHT) + 'px';
    // paddingTop is inside height, so set box-sizing
    container.style.boxSizing = 'border-box';

    for (let i = start; i < end; i++) {
        const row = rows[i];
        const div = document.createElement('div');
        div.className = 'trace-text-row';
        div.setAttribute('data-row', i);
        div.setAttribute('data-id', row.id);

        // Time
        const timeSpan = document.createElement('span');
        timeSpan.className = 'time';
        timeSpan.textContent = formatTime(row.timestamp);
        div.appendChild(timeSpan);

        // Scope
        const scopeSpan = document.createElement('span');
        scopeSpan.className = 'scope';
        scopeSpan.textContent = textScopeName(row.scope);
        div.appendChild(scopeSpan);

        // Name with type label
        const nameSpan = document.createElement('span');
        nameSpan.className = 'name';
        let prefix = '';
        if (row.type === 'open') prefix = '[开始] ';
        else if (row.type === 'close') prefix = '[完成] ';
        nameSpan.textContent = prefix + (row.name || '');
        div.appendChild(nameSpan);

        // Detail tag
        if (row.detail) {
            const tagSpan = document.createElement('span');
            tagSpan.className = 'tag';
            tagSpan.textContent = row.detail.length > 50
                ? row.detail.substring(0, 50) + '...'
                : row.detail;
            div.appendChild(tagSpan);
        }

        container.appendChild(div);
    }
}

// --- Time formatting ---

function formatTime(timestamp) {
    if (!timestamp) return '';
    const d = new Date(timestamp);
    const h = String(d.getHours()).padStart(2, '0');
    const m = String(d.getMinutes()).padStart(2, '0');
    const s = String(d.getSeconds()).padStart(2, '0');
    const ms = String(Math.floor(d.getMilliseconds() / 100));
    return `${h}:${m}:${s}.${ms}`;
}

// --- Virtual rendering: only render visible range + buffer ---

function renderVisibleRange() {
    const { graphEl, textEl, rows, rowMeta, columns, _contentGroup } = state;
    if (!graphEl || !rows || rows.length === 0 || !_contentGroup) return;

    const scrollTop = graphEl.scrollTop;
    const viewHeight = graphEl.clientHeight;

    const firstVisible = Math.floor(scrollTop / ROW_HEIGHT);
    const lastVisible = Math.ceil((scrollTop + viewHeight) / ROW_HEIGHT);
    const renderStart = Math.max(0, firstVisible - VIRTUAL_BUFFER);
    const renderEnd = Math.min(rows.length, lastVisible + VIRTUAL_BUFFER);

    // Skip if range hasn't changed
    if (renderStart === state._renderStart && renderEnd === state._renderEnd) return;
    state._renderStart = renderStart;
    state._renderEnd = renderEnd;

    // Clear content group and re-render
    _contentGroup.innerHTML = '';

    renderVerticalLinesRange(_contentGroup, columns, renderStart, renderEnd);
    renderEngineLifecycleLines(_contentGroup, rows, rowMeta, columns, renderStart, renderEnd);
    renderCrossLinesRange(_contentGroup, rows, rowMeta, columns, renderStart, renderEnd);
    renderNodesRange(_contentGroup, rows, rowMeta, columns, renderStart, renderEnd);

    // Re-render text rows
    renderTextRange(renderStart, renderEnd);
}

function scheduleRender() {
    if (state._rafPending) return;
    state._rafPending = true;
    requestAnimationFrame(() => {
        state._rafPending = false;
        renderVisibleRange();
    });
}

// --- Scroll sync ---

function setupScrollSync(graphEl, textEl) {
    const onGraphScroll = () => {
        if (state.scrollSyncing) return;
        state.scrollSyncing = true;
        textEl.scrollTop = graphEl.scrollTop;
        state.scrollSyncing = false;
        scheduleRender();
    };

    const onTextScroll = () => {
        if (state.scrollSyncing) return;
        state.scrollSyncing = true;
        graphEl.scrollTop = textEl.scrollTop;
        state.scrollSyncing = false;
        scheduleRender();
    };

    graphEl.addEventListener('scroll', onGraphScroll);
    textEl.addEventListener('scroll', onTextScroll);

    // Store for cleanup
    state._scrollCleanup = () => {
        graphEl.removeEventListener('scroll', onGraphScroll);
        textEl.removeEventListener('scroll', onTextScroll);
    };
}

// --- Causal chain hover interaction ---

function buildEffectsOf(causeSpanIdToRowId) {
    const effectsOf = {};
    for (const [effectId, causeId] of Object.entries(causeSpanIdToRowId)) {
        const eid = parseInt(effectId);
        if (!effectsOf[causeId]) effectsOf[causeId] = [];
        effectsOf[causeId].push(eid);
    }
    return effectsOf;
}

function setupInteraction(graphEl, textEl, bodyEl) {
    function getAncestors(id) {
        const { byId } = state;
        const ancestors = new Set();
        let current = byId[id];
        while (current && current._resolvedParentId != null) {
            ancestors.add(current._resolvedParentId);
            current = byId[current._resolvedParentId];
        }
        return ancestors;
    }

    function getDescendants(id) {
        const { childrenOf } = state;
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
        const { rows, byId, closeToOpen, causeSpanIdToRowId, effectsOf } = state;
        const row = rows[rowIdx];
        let targetId = row.id;

        if (row.type === 'close' && closeToOpen[row.id]) {
            targetId = closeToOpen[row.id];
        }

        const highlighted = new Set([targetId]);
        for (const a of getAncestors(targetId)) highlighted.add(a);
        for (const d of getDescendants(targetId)) highlighted.add(d);
        if (row.type === 'close') highlighted.add(row.id);
        if (row.type === 'open' && row.spanId) {
            const closeRow = rows.find(r => r.type === 'close' && r.spanId === row.spanId);
            if (closeRow) highlighted.add(closeRow.id);
        }

        function addWithPair(id) {
            highlighted.add(id);
            const r = byId[id];
            if (r?.type === 'open' && r.spanId) {
                const cp = rows.find(x => x.type === 'close' && x.spanId === r.spanId);
                if (cp) highlighted.add(cp.id);
            }
        }

        // Upstream: walk cause chain
        let cur = targetId;
        while (true) {
            const causeId = causeSpanIdToRowId[cur];
            if (causeId == null || highlighted.has(causeId)) break;
            addWithPair(causeId);
            for (const a of getAncestors(causeId)) highlighted.add(a);
            cur = causeId;
        }

        // Downstream: walk effects
        const downQueue = [targetId];
        const downVisited = new Set([targetId]);
        while (downQueue.length > 0) {
            const cid = downQueue.shift();
            const effects = effectsOf[cid] || [];
            for (const eid of effects) {
                if (downVisited.has(eid)) continue;
                downVisited.add(eid);
                addWithPair(eid);
                for (const d of getDescendants(eid)) highlighted.add(d);
                downQueue.push(eid);
            }
        }

        return highlighted;
    }

    function applyHighlight(rowIdx) {
        clearHighlight();
        const { rows } = state;
        state._lastHighlightRowId = rows[rowIdx]?.id ?? -1;
        const highlighted = getHighlightSet(rowIdx);
        bodyEl.classList.add('has-hover');

        graphEl.querySelectorAll('.node, .v-line').forEach(el => {
            const id = parseInt(el.dataset.id);
            if (!isNaN(id) && !highlighted.has(id)) {
                el.classList.add('dimmed');
            }
        });

        graphEl.querySelectorAll('.cross-line').forEach(el => {
            const from = parseInt(el.dataset.from);
            const to = parseInt(el.dataset.to);
            if (!highlighted.has(from) || !highlighted.has(to)) {
                el.classList.add('dimmed');
            }
        });

        textEl.querySelectorAll('.trace-text-row').forEach(el => {
            const id = parseInt(el.dataset.id);
            if (!isNaN(id) && !highlighted.has(id)) {
                el.classList.add('dimmed');
            }
            if (id === rows[rowIdx].id) {
                el.classList.add('row-active');
            }
        });

        let hlRect = graphEl.querySelector('.row-highlight');
        if (!hlRect) {
            hlRect = svgEl('rect', { class: 'row-highlight' });
            state.svgEl.insertBefore(hlRect, state._contentGroup);
        }
        const totalWidth = state.columns.reduce((s, c) => s + c.width, 0);
        hlRect.setAttribute('x', 0);
        hlRect.setAttribute('y', rowIdx * ROW_HEIGHT);
        hlRect.setAttribute('width', totalWidth);
        hlRect.setAttribute('height', ROW_HEIGHT);
        hlRect.style.display = '';
    }

    function clearHighlight() {
        state._lastHighlightRowId = -1;
        bodyEl.classList.remove('has-hover');
        graphEl.querySelectorAll('.dimmed').forEach(el => el.classList.remove('dimmed'));
        textEl.querySelectorAll('.dimmed').forEach(el => el.classList.remove('dimmed'));
        textEl.querySelectorAll('.row-active').forEach(el => el.classList.remove('row-active'));
        const hlRect = graphEl.querySelector('.row-highlight');
        if (hlRect) hlRect.style.display = 'none';
    }

    state._applyHighlight = applyHighlight;

    // Detail lock state
    let _detailLockedId = -1;
    function lockDetail(rowId) {
        _detailLockedId = rowId;
        if (state.detailEl) state.detailEl.classList.add('locked');
        showDetail(rowId);
    }
    function unlockDetail() {
        _detailLockedId = -1;
        if (state.detailEl) {
            state.detailEl.classList.remove('locked');
            state.detailEl.innerHTML = '<div class="detail-hint">悬停查看详情 · 单击锁定 · 双击筛选信号 · 右键解锁</div>';
        }
    }
    function isDetailLocked() { return _detailLockedId >= 0; }
    unlockDetail(); // show hint initially

    function getRowIdxFromEvent(e) {
        // Try graph node first
        const node = e.target.closest('.node');
        if (node) {
            const id = parseInt(node.dataset.id);
            if (!isNaN(id)) return state.rows.findIndex(r => r.id === id);
        }
        // Try text row
        const textRow = e.target.closest('.trace-text-row');
        if (textRow) {
            const ri = parseInt(textRow.dataset.row);
            if (!isNaN(ri)) return ri;
        }
        return -1;
    }

    // --- Hover: highlight + show detail (if not locked) ---
    let _lastHoverRow = -1;
    graphEl.addEventListener('mousemove', e => {
        const svg = graphEl.querySelector('svg');
        if (!svg) return;
        const rect = svg.getBoundingClientRect();
        const y = e.clientY - rect.top;
        const ri = Math.floor(y / ROW_HEIGHT);
        if (ri === _lastHoverRow) return;
        _lastHoverRow = ri;
        if (ri >= 0 && ri < state.rows.length) {
            state.hoverPaused = true;
            clearTimeout(state._resumeTimer);
            state._resumeTimer = null;
            applyHighlight(ri);
            if (!isDetailLocked()) showDetail(state.rows[ri].id);
        } else {
            clearHighlight();
            if (!isDetailLocked()) unlockDetail();
        }
    });
    graphEl.addEventListener('mouseleave', () => {
        _lastHoverRow = -1;
        clearHighlight();
        if (!isDetailLocked()) unlockDetail();
        if (state.hoverPaused && !state._resumeTimer) {
            state._resumeTimer = setTimeout(resumeAutoScroll, 2000);
        }
    });

    let _lastTextHoverId = -1;
    textEl.addEventListener('mousemove', e => {
        const row = e.target.closest('.trace-text-row');
        if (!row) {
            if (state.hoverPaused && !state._resumeTimer) {
                state._resumeTimer = setTimeout(resumeAutoScroll, 2000);
            }
            return;
        }
        clearTimeout(state._resumeTimer);
        state._resumeTimer = null;
        state.hoverPaused = true;
        const id = parseInt(row.dataset.id);
        if (isNaN(id) || id === _lastTextHoverId) return;
        _lastTextHoverId = id;
        const ri = state.rows.findIndex(r => r.id === id);
        if (ri >= 0) {
            applyHighlight(ri);
            if (!isDetailLocked()) showDetail(state.rows[ri].id);
        }
    });
    textEl.addEventListener('mouseleave', () => {
        _lastTextHoverId = -1;
        clearHighlight();
        if (!isDetailLocked()) unlockDetail();
        if (state.hoverPaused && !state._resumeTimer) {
            state._resumeTimer = setTimeout(resumeAutoScroll, 2000);
        }
    });

    // --- Click: toggle detail lock ---
    function onClick(e) {
        const ri = getRowIdxFromEvent(e);
        if (ri < 0) return;
        const rowId = state.rows[ri].id;
        if (_detailLockedId === rowId) {
            unlockDetail();
        } else {
            lockDetail(rowId);
        }
        e.stopPropagation();
    }
    graphEl.addEventListener('click', onClick);
    textEl.addEventListener('click', onClick);

    // --- Double-click: filter by signal ---
    function onDblClick(e) {
        const ri = getRowIdxFromEvent(e);
        if (ri < 0) return;
        filterSignal(state.rows[ri].signalId);
    }
    graphEl.addEventListener('dblclick', onDblClick);
    textEl.addEventListener('dblclick', onDblClick);

    // --- Right-click: unlock detail ---
    function onContextMenu(e) {
        if (isDetailLocked()) {
            e.preventDefault();
            unlockDetail();
        }
    }
    graphEl.addEventListener('contextmenu', onContextMenu);
    textEl.addEventListener('contextmenu', onContextMenu);

    // Wheel on either panel resumes auto-scroll immediately
    function resumeAutoScroll() {
        state.hoverPaused = false;
        clearTimeout(state._resumeTimer);
        state._resumeTimer = null;
        if (state.textEl && state.graphEl) {
            state.textEl.scrollTop = state.textEl.scrollHeight;
            state.graphEl.scrollTop = state.graphEl.scrollHeight;
        }
    }
    textEl.addEventListener('wheel', () => { state.hoverPaused = false; clearTimeout(state._resumeTimer); state._resumeTimer = null; });
    graphEl.addEventListener('wheel', () => { state.hoverPaused = false; clearTimeout(state._resumeTimer); state._resumeTimer = null; });
}

// --- Signal filter helper ---

function filterSignal(signalId) {
    const select = document.querySelector('.log-trace-toolbar select');
    if (select) {
        for (const opt of select.options) {
            if (opt.value === signalId) {
                select.value = signalId;
                select.dispatchEvent(new Event('change', { bubbles: true }));
                return;
            }
        }
    }
}

// --- Exported functions ---

export function renderTrace(graphEl, textEl, bodyEl, detailEl, scopes, rows) {
    // Initialize state
    state = initState();
    state.graphEl = graphEl;
    state.textEl = textEl;
    state.bodyEl = bodyEl;
    state.detailEl = detailEl;
    state.scopes = sortScopes(scopes);
    state.rows = rows;

    if (!rows || rows.length === 0) return;

    // Build lookup maps for interaction
    const { byId, childrenOf, closeToOpen, causeSpanIdToRowId, engineLifecycles, startupSignalClosed, ceaseIndices } = buildLookupMaps(rows);
    state.byId = byId;
    state.childrenOf = childrenOf;
    state.closeToOpen = closeToOpen;
    state.causeSpanIdToRowId = causeSpanIdToRowId;
    state.effectsOf = buildEffectsOf(causeSpanIdToRowId);
    state.engineLifecycles = engineLifecycles;
    state.startupSignalClosed = startupSignalClosed;
    state.ceaseIndices = ceaseIndices;

    // Assign slots
    const { meta, maxSlots } = assignSlots(rows, state.scopes);
    state.rowMeta = meta;

    // Compute column layout
    const columns = computeColumns(state.scopes, maxSlots);
    state.columns = columns;

    // Render
    renderHeader(graphEl, columns);
    renderSvg(graphEl, columns, rows, meta);
    renderTextRows(textEl, rows);
    setupScrollSync(graphEl, textEl);

    // Initial virtual render (after DOM is ready), then scroll to bottom
    requestAnimationFrame(() => {
        renderVisibleRange();
        textEl.scrollTop = textEl.scrollHeight;
        graphEl.scrollTop = graphEl.scrollHeight;
    });

    setupInteraction(graphEl, textEl, bodyEl);
}

// --- Detail panel ---

function isSignalOrigin(row) {
    if (row.parentId == null && row.causeSpanId == null) return true;
    if (row.spanId != null && state.causeSpanIdToRowId) {
        for (const [effectId, causeId] of Object.entries(state.causeSpanIdToRowId)) {
            if (row.spanId === state.rows.find(r => r.id === causeId)?.spanId) return true;
        }
    }
    return false;
}

function showDetail(rowId) {
    const el = state.detailEl;
    if (!el) return;
    const row = state.byId[rowId];
    if (!row) return;

    const ts = new Date(row.timestamp).toLocaleString();
    let detail = '';
    try { detail = row.detail ? JSON.stringify(JSON.parse(row.detail), null, 2) : ''; }
    catch { detail = row.detail || ''; }

    const origin = isSignalOrigin(row);
    const name = row.name || '(close)';
    const prefix = row.type === 'open' ? '[开始] ' : row.type === 'close' ? '[完成] ' : '';
    const scopeLabel = textScopeName(row.scope);

    let html = `<div class="detail-title">${prefix}${escapeHtml(name)}</div>`;
    html += `<div class="detail-subtitle">${escapeHtml(scopeLabel)} · ${escapeHtml(ts)}</div>`;

    if (origin) {
        html += `<div class="detail-signal-badge" title="${escapeHtml(row.signalId)}">◉ 信号源 · ${escapeHtml(row.signalId)}</div>`;
    }

    html += `<div class="detail-fields">`;
    const fields = [
        { label: '类型', value: row.type },
        { label: 'Group', value: row.groupName },
        { label: 'Signal', value: row.signalId },
        { label: 'Span', value: row.spanId || '-' },
        { label: 'Parent', value: row.parentId || '-' },
        { label: 'Cause', value: row.causeSpanId || '-' },
        { label: 'Level', value: ['Debug','Info','Warn','Error'][row.level] || row.level },
    ];
    for (const f of fields) {
        html += `<div class="detail-field"><div class="detail-label">${f.label}</div><div class="detail-value">${escapeHtml(f.value ?? '')}</div></div>`;
    }
    html += `</div>`;

    if (detail) {
        html += `<div class="detail-json">${escapeHtml(detail)}</div>`;
    }

    html += `<button class="detail-copy-btn" title="复制详情" onclick="navigator.clipboard.writeText(this.previousElementSibling?.textContent || '')">📋 复制</button>`;

    el.innerHTML = html;
}

function escapeHtml(s) {
    return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}

let _appendPending = false;
let _appendAutoScroll = false;

export function appendRow(row, autoScroll) {
    appendRows([row], autoScroll);
}

export function appendRows(rows, autoScroll) {
    if (!state || !state.graphEl) return;
    for (const row of rows) {
        state.rows.push(row);
    }
    while (state.rows.length > MAX_ROWS) state.rows.shift();
    _appendAutoScroll = _appendAutoScroll || autoScroll;
    if (!_appendPending) {
        _appendPending = true;
        const highlightId = state._lastHighlightRowId;
        requestAnimationFrame(() => {
            _appendPending = false;
            rebuildState();
            // Restore highlight if one was active before rebuild
            if (highlightId >= 0 && state._applyHighlight) {
                const ri = state.rows.findIndex(r => r.id === highlightId);
                if (ri >= 0) state._applyHighlight(ri);
            }
            // Skip auto-scroll if user is hovering on a row
            if (_appendAutoScroll && state.textEl && !state.hoverPaused) {
                state.textEl.scrollTop = state.textEl.scrollHeight;
                state.graphEl.scrollTop = state.graphEl.scrollHeight;
            }
            _appendAutoScroll = false;
        });
    }
}

function rebuildState() {
    if (!state || state.rows.length === 0) return;
    const rows = state.rows;
    const scopes = sortScopes([...new Set(rows.map(r => r.scope))]);
    state.scopes = scopes;

    const { byId, childrenOf, closeToOpen, causeSpanIdToRowId, engineLifecycles, startupSignalClosed, ceaseIndices } = buildLookupMaps(rows);
    state.byId = byId;
    state.childrenOf = childrenOf;
    state.closeToOpen = closeToOpen;
    state.causeSpanIdToRowId = causeSpanIdToRowId;
    state.effectsOf = buildEffectsOf(causeSpanIdToRowId);
    state.engineLifecycles = engineLifecycles;
    state.startupSignalClosed = startupSignalClosed;
    state.ceaseIndices = ceaseIndices;

    const { meta, maxSlots } = assignSlots(rows, scopes);
    state.rowMeta = meta;

    const columns = computeColumns(scopes, maxSlots);
    state.columns = columns;

    renderHeader(state.graphEl, columns);
    renderSvg(state.graphEl, columns, rows, meta);
    renderTextRows(state.textEl, rows);
    state._renderStart = -1;
    state._renderEnd = -1;
    renderVisibleRange();
}

export function prependRows(olderRows, hasMore) {
    if (!state || !state.graphEl) return;
    state.rows = [...olderRows, ...state.rows];
    while (state.rows.length > MAX_ROWS) state.rows.pop();
    rebuildState();
}

export function dispose() {
    if (state && state._scrollCleanup) {
        state._scrollCleanup();
    }
    state = null;
}