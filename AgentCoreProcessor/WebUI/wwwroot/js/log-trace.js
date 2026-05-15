// log-trace.js — 信号追踪 SVG 渲染引擎
// Blazor 通过 IJSRuntime 导入此 ES module

const ROW_HEIGHT = 26;
const SLOT_WIDTH = 14;
const NODE_SIZE = 9;
const LINE_WIDTH = 1.5;
const CROSS_LINE_WIDTH = 2.5;
const COL_PADDING = 12;
const MIN_COL_WIDTH = 180;

const SVG_NS = 'http://www.w3.org/2000/svg';

// Module-level state
let state = null;

function initState() {
    return {
        graphEl: null,
        textEl: null,
        bodyEl: null,
        scopes: [],
        rows: [],
        columns: [],      // { scope, slotCount, width, x }
        rowMeta: [],      // per-row: { _slotIdx, _scopeIdx, _x, _y }
        svgEl: null,
        headerEl: null,
        scrollSyncing: false,
        byId: {},         // id → row object
        childrenOf: {},   // parentId → [child ids]
        closeToOpen: {}   // close row id → paired open row id (matched by spanId)
    };
}

// --- Build lookup maps for interaction ---

function buildLookupMaps(rows) {
    const byId = {};
    const childrenOf = {};
    const closeToOpen = {};

    // parent_id in the DB stores the parent's TIMESTAMP, not its row id.
    // Build a timestamp→id map to resolve parent references.
    const idByTimestamp = {};
    for (const row of rows) {
        byId[row.id] = row;
        // For open events, their timestamp is what children reference as parentId
        if (row.type === 'open') {
            idByTimestamp[row.timestamp] = row.id;
        }
    }

    // Build childrenOf using resolved parent ids
    for (const row of rows) {
        if (row.parentId != null) {
            // parentId is a timestamp — resolve to actual row id
            const parentRowId = idByTimestamp[row.parentId] ?? row.parentId;
            row._resolvedParentId = parentRowId;
            if (!childrenOf[parentRowId]) childrenOf[parentRowId] = [];
            childrenOf[parentRowId].push(row.id);
        }
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

    return { byId, childrenOf, closeToOpen };
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

        if (row.type === 'open') {
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
    if (scope.startsWith('channel:')) return scope.slice(8);
    if (scope.startsWith('adapter:')) return scope.slice(8);
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

// --- Render SVG ---

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

    // Column backgrounds and separators
    for (let i = 0; i < columns.length; i++) {
        const col = columns[i];
        // Odd columns get subtle tint
        if (i % 2 === 1) {
            svg.appendChild(svgEl('rect', {
                x: col.x, y: 0, width: col.width, height: totalHeight,
                fill: 'rgba(255,255,255,0.015)', class: 'col-bg'
            }));
        }
        // Dashed separator (except after last)
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

    // Render vertical lines
    renderVerticalLines(svg, rows, meta, columns);

    // Render cross-scope lines
    renderCrossLines(svg, rows, meta, columns);

    // Render nodes
    renderNodes(svg, rows, meta, columns);

    graphEl.appendChild(svg);
    state.svgEl = svg;
}

// --- Render vertical lines ---

function renderVerticalLines(svg, rows, meta, columns) {
    // Build span map: spanId -> { openIdx, closeIdx, scopeIdx, slotIdx, openRowId }
    const spans = new Map();

    for (let i = 0; i < rows.length; i++) {
        const row = rows[i];
        const m = meta[i];
        if (row.type === 'open') {
            spans.set(row.spanId, { openIdx: i, closeIdx: -1, scopeIdx: m._scopeIdx, slotIdx: m._slotIdx, openRowId: row.id });
        } else if (row.type === 'close') {
            const span = spans.get(row.spanId);
            if (span) span.closeIdx = i;
        }
    }

    for (const [spanId, span] of spans) {
        const { openIdx, closeIdx, scopeIdx, slotIdx, openRowId } = span;
        const x = getNodeX(columns, scopeIdx, slotIdx);
        const endIdx = closeIdx >= 0 ? closeIdx : rows.length - 1;

        for (let r = openIdx; r <= endIdx; r++) {
            let y1, y2;
            if (r === openIdx) {
                // Half-line: bottom half
                y1 = getNodeY(r);
                y2 = r * ROW_HEIGHT + ROW_HEIGHT;
            } else if (r === endIdx && closeIdx >= 0) {
                // Half-line: top half
                y1 = r * ROW_HEIGHT;
                y2 = getNodeY(r);
            } else {
                // Full-height segment
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
            svg.appendChild(line);
        }
    }
}

// --- Render cross-scope diagonal lines ---

function renderCrossLines(svg, rows, meta, columns) {
    // Build row index by id for quick lookup
    const rowById = new Map();
    for (let i = 0; i < rows.length; i++) {
        rowById.set(rows[i].id, i);
    }

    for (let i = 0; i < rows.length; i++) {
        const row = rows[i];
        if (row._resolvedParentId == null) continue;

        const parentIdx = rowById.get(row._resolvedParentId);
        if (parentIdx == null) continue;

        const parentMeta = meta[parentIdx];
        const childMeta = meta[i];

        // Only draw if different scope
        if (parentMeta._scopeIdx === childMeta._scopeIdx) continue;

        const x1 = getNodeX(columns, parentMeta._scopeIdx, parentMeta._slotIdx);
        const y1 = getNodeY(parentIdx);
        const x2 = getNodeX(columns, childMeta._scopeIdx, childMeta._slotIdx);
        const y2 = getNodeY(i);

        const line = svgEl('line', {
            x1, y1, x2, y2,
            stroke: 'var(--vis-line-cross)',
            'stroke-width': CROSS_LINE_WIDTH,
            class: 'cross-line',
            'data-from': rows[parentIdx].id,
            'data-to': row.id
        });
        svg.appendChild(line);
    }
}

// --- Render nodes ---

function renderNodes(svg, rows, meta, columns) {
    // Build span map to check if open has paired close
    const spanHasClose = new Set();
    for (const row of rows) {
        if (row.type === 'close') spanHasClose.add(row.spanId);
    }

    for (let i = 0; i < rows.length; i++) {
        const row = rows[i];
        const m = meta[i];
        const cx = getNodeX(columns, m._scopeIdx, m._slotIdx);
        const cy = getNodeY(i);
        const r = NODE_SIZE / 2;

        let node;

        if (row.type === 'open' && row.parentId == null) {
            // Origin: double circle
            const g = svgEl('g', { class: 'node', 'data-row': i, 'data-id': row.id });
            g.appendChild(svgEl('circle', {
                cx, cy, r: r + 2,
                fill: 'none', stroke: 'var(--vis-info)', 'stroke-width': 1.5
            }));
            g.appendChild(svgEl('circle', {
                cx, cy, r: r - 1,
                fill: 'none', stroke: 'var(--vis-info)', 'stroke-width': 1.5
            }));
            node = g;
        } else if (row.type === 'open') {
            // Ring: green if paired, red if stuck
            const color = spanHasClose.has(row.spanId) ? 'var(--vis-ok)' : 'var(--vis-error)';
            node = svgEl('circle', {
                cx, cy, r,
                fill: 'none', stroke: color, 'stroke-width': 1.5,
                class: 'node', 'data-row': i, 'data-id': row.id
            });
        } else if (row.type === 'close') {
            // Ring: green
            node = svgEl('circle', {
                cx, cy, r,
                fill: 'none', stroke: 'var(--vis-ok)', 'stroke-width': 1.5,
                class: 'node', 'data-row': i, 'data-id': row.id
            });
        } else {
            // Event: filled circle
            const color = (row.level === 0) ? 'var(--vis-debug)' : 'var(--vis-info)';
            node = svgEl('circle', {
                cx, cy, r: r - 1,
                fill: color, stroke: 'none',
                class: 'node', 'data-row': i, 'data-id': row.id
            });
        }

        svg.appendChild(node);
    }
}

// --- Render text rows ---

function renderTextRows(textEl, rows) {
    textEl.innerHTML = '';

    // Add a header spacer to align with the graph column header
    const headerSpacer = document.createElement('div');
    headerSpacer.className = 'log-trace-header';
    headerSpacer.style.borderBottom = '1px solid var(--vis-col-separator)';
    const timeLabel = document.createElement('span');
    timeLabel.style.cssText = 'width:80px;flex-shrink:0;font-size:12px;color:var(--vis-text-dim);';
    timeLabel.textContent = '时间';
    const nameLabel = document.createElement('span');
    nameLabel.style.cssText = 'flex:1;font-size:12px;color:var(--vis-text-dim);padding-left:0.5rem;';
    nameLabel.textContent = '事件';
    headerSpacer.appendChild(timeLabel);
    headerSpacer.appendChild(nameLabel);
    textEl.appendChild(headerSpacer);

    for (let i = 0; i < rows.length; i++) {
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

        textEl.appendChild(div);
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

// --- Scroll sync ---

function setupScrollSync(graphEl, textEl) {
    const onGraphScroll = () => {
        if (state.scrollSyncing) return;
        state.scrollSyncing = true;
        textEl.scrollTop = graphEl.scrollTop;
        state.scrollSyncing = false;
    };

    const onTextScroll = () => {
        if (state.scrollSyncing) return;
        state.scrollSyncing = true;
        graphEl.scrollTop = textEl.scrollTop;
        state.scrollSyncing = false;
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

function setupInteraction(graphEl, textEl, bodyEl) {
    const { rows, byId, childrenOf, closeToOpen } = state;

    function getAncestors(id) {
        const ancestors = new Set();
        let current = byId[id];
        while (current && current._resolvedParentId != null) {
            ancestors.add(current._resolvedParentId);
            current = byId[current._resolvedParentId];
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

        // close → use paired open's id for tree traversal
        if (row.type === 'close' && closeToOpen[row.id]) {
            targetId = closeToOpen[row.id];
        }

        const highlighted = new Set([targetId]);
        for (const a of getAncestors(targetId)) highlighted.add(a);
        for (const d of getDescendants(targetId)) highlighted.add(d);
        // Also highlight the close/open pair itself
        if (row.type === 'close') highlighted.add(row.id);
        if (row.type === 'open' && row.spanId) {
            const closeRow = rows.find(r => r.type === 'close' && r.spanId === row.spanId);
            if (closeRow) highlighted.add(closeRow.id);
        }

        return highlighted;
    }

    function applyHighlight(rowIdx) {
        const highlighted = getHighlightSet(rowIdx);
        bodyEl.classList.add('has-hover');

        // Dim SVG elements by data-id
        graphEl.querySelectorAll('.node, .v-line').forEach(el => {
            const id = parseInt(el.dataset.id);
            if (!isNaN(id) && !highlighted.has(id)) {
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
            const id = parseInt(el.dataset.id);
            if (!isNaN(id) && !highlighted.has(id)) {
                el.classList.add('dimmed');
            }
        });
    }

    function clearHighlight() {
        bodyEl.classList.remove('has-hover');
        graphEl.querySelectorAll('.dimmed').forEach(el => el.classList.remove('dimmed'));
        textEl.querySelectorAll('.dimmed').forEach(el => el.classList.remove('dimmed'));
    }

    // Event delegation on graph SVG
    graphEl.addEventListener('mouseover', e => {
        const node = e.target.closest('.node');
        if (!node) return;
        const id = parseInt(node.dataset.id);
        if (isNaN(id)) return;
        const ri = rows.findIndex(r => r.id === id);
        if (ri >= 0) applyHighlight(ri);
    });
    graphEl.addEventListener('mouseout', e => {
        if (e.target.closest('.node')) clearHighlight();
    });

    // Event delegation on text rows
    textEl.addEventListener('mouseover', e => {
        const row = e.target.closest('.trace-text-row');
        if (!row) return;
        const id = parseInt(row.dataset.id);
        if (isNaN(id)) return;
        const ri = rows.findIndex(r => r.id === id);
        if (ri >= 0) applyHighlight(ri);
    });
    textEl.addEventListener('mouseout', e => {
        if (e.target.closest('.trace-text-row')) clearHighlight();
    });
}

// --- Context menu ---

function setupContextMenu(graphEl, textEl) {
    let menu = null;

    function showMenu(e, rowIdx) {
        e.preventDefault();
        hideMenu();
        const row = state.rows[rowIdx];
        if (!row) return;

        menu = document.createElement('div');
        menu.className = 'trace-context-menu';

        const items = [];
        items.push({ label: '查看此信号', icon: 'bi-diagram-3', action: () => filterSignal(row.signalId) });

        if ((row.type === 'open' || row.type === 'close') && row.spanId) {
            const pairType = row.type === 'open' ? 'close' : 'open';
            const pair = state.rows.findIndex(r => r.spanId === row.spanId && r.type === pairType);
            if (pair >= 0) {
                items.push({ label: '定位配对节点', icon: 'bi-arrow-left-right', action: () => scrollToRow(pair) });
            }
        }

        if (row._resolvedParentId != null) {
            const parentIdx = state.rows.findIndex(r => r.id === row._resolvedParentId);
            if (parentIdx >= 0) {
                items.push({ label: '跳转到父节点', icon: 'bi-arrow-up', action: () => scrollToRow(parentIdx) });
            }
        }

        items.push({ sep: true });
        items.push({ label: '复制详情', icon: 'bi-clipboard', action: () => copyDetail(row) });

        for (const item of items) {
            if (item.sep) {
                const sep = document.createElement('div');
                sep.className = 'menu-sep';
                menu.appendChild(sep);
            } else {
                const div = document.createElement('div');
                div.className = 'menu-item';
                div.innerHTML = `<i class="bi ${item.icon}"></i>${item.label}`;
                div.addEventListener('click', () => { item.action(); hideMenu(); });
                menu.appendChild(div);
            }
        }

        // Position menu
        menu.style.left = e.clientX + 'px';
        menu.style.top = e.clientY + 'px';
        document.body.appendChild(menu);

        // Adjust if off-screen
        requestAnimationFrame(() => {
            if (!menu) return;
            const rect = menu.getBoundingClientRect();
            if (rect.right > window.innerWidth) menu.style.left = (e.clientX - rect.width) + 'px';
            if (rect.bottom > window.innerHeight) menu.style.top = (e.clientY - rect.height) + 'px';
        });
    }

    function hideMenu() {
        if (menu) { menu.remove(); menu = null; }
    }

    function scrollToRow(rowIdx) {
        const y = rowIdx * ROW_HEIGHT - state.graphEl.clientHeight / 2;
        state.graphEl.scrollTop = Math.max(0, y);
        state.textEl.scrollTop = Math.max(0, y);
        // Flash highlight
        const textRow = state.textEl.querySelector(`[data-row="${rowIdx}"]`);
        if (textRow) {
            textRow.style.background = 'rgba(96,165,250,0.2)';
            setTimeout(() => { textRow.style.background = ''; }, 1500);
        }
    }

    function filterSignal(signalId) {
        // Find the select element and change its value, then trigger change
        const select = document.querySelector('.log-trace-toolbar select');
        if (select) {
            // Find option matching this signalId
            for (const opt of select.options) {
                if (opt.value === signalId) {
                    select.value = signalId;
                    select.dispatchEvent(new Event('change', { bubbles: true }));
                    return;
                }
            }
        }
    }

    function copyDetail(row) {
        const text = row.detail || row.name || '';
        navigator.clipboard.writeText(text).catch(() => {});
    }

    // Bind right-click on graph nodes
    graphEl.addEventListener('contextmenu', e => {
        const node = e.target.closest('.node');
        if (!node) return;
        const id = parseInt(node.dataset.id);
        if (isNaN(id)) return;
        const ri = state.rows.findIndex(r => r.id === id);
        if (ri >= 0) showMenu(e, ri);
    });

    // Bind right-click on text rows
    textEl.addEventListener('contextmenu', e => {
        const row = e.target.closest('.trace-text-row');
        if (!row) return;
        const ri = parseInt(row.dataset.row);
        if (!isNaN(ri)) showMenu(e, ri);
    });

    // Close on click anywhere
    document.addEventListener('click', hideMenu);
    document.addEventListener('contextmenu', e => {
        if (!e.target.closest('.node') && !e.target.closest('.trace-text-row')) hideMenu();
    });
}

// --- Exported functions ---

export function renderTrace(graphEl, textEl, bodyEl, scopes, rows) {
    // Initialize state
    state = initState();
    state.graphEl = graphEl;
    state.textEl = textEl;
    state.bodyEl = bodyEl;
    state.scopes = scopes;
    state.rows = rows;

    if (!rows || rows.length === 0) return;

    // Build lookup maps for interaction
    const { byId, childrenOf, closeToOpen } = buildLookupMaps(rows);
    state.byId = byId;
    state.childrenOf = childrenOf;
    state.closeToOpen = closeToOpen;

    // Assign slots
    const { meta, maxSlots } = assignSlots(rows, scopes);
    state.rowMeta = meta;

    // Compute column layout
    const columns = computeColumns(scopes, maxSlots);
    state.columns = columns;

    // Render
    renderHeader(graphEl, columns);
    renderSvg(graphEl, columns, rows, meta);
    renderTextRows(textEl, rows);
    setupScrollSync(graphEl, textEl);
    setupInteraction(graphEl, textEl, bodyEl);
    setupContextMenu(graphEl, textEl);
}

export function appendRow(row, autoScroll) {
    // Stub: full re-render for now
    if (!state || !state.graphEl) return;
    state.rows.push(row);
    renderTrace(state.graphEl, state.textEl, state.bodyEl, state.scopes, state.rows);
    if (autoScroll && state.textEl) {
        state.textEl.scrollTop = state.textEl.scrollHeight;
        state.graphEl.scrollTop = state.graphEl.scrollHeight;
    }
}

export function prependRows(olderRows, hasMore) {
    // Stub: full re-render for now
    if (!state || !state.graphEl) return;
    state.rows = [...olderRows, ...state.rows];
    renderTrace(state.graphEl, state.textEl, state.bodyEl, state.scopes, state.rows);
}

export function dispose() {
    if (state && state._scrollCleanup) {
        state._scrollCleanup();
    }
    state = null;
}