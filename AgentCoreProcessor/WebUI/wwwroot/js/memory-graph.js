var cy = null;
var dotNetRef = null;
var focusId = null;
var currentZoom = 0.8;

window.initMemoryGraph = function (containerId, data, coreId, dotnet) {
    dotNetRef = dotnet;
    focusId = coreId;

    var container = document.getElementById(containerId);
    if (!container) return;

    if (cy) { cy.destroy(); cy = null; currentZoom = 0.8; }

    var els = buildElements(data.nodes, data.edges);

    try {
        cy = cytoscape({
            container: container,
            elements: els,
            style: makeStyles(),
            layout: { name: 'preset', fit: false, padding: 0 },
            wheelSensitivity: 0.3,
            minZoom: 0.02,
            maxZoom: 20
        });

        setupZoomCompensation();
        setupEvents(container);

        // Initial center on focus
        setTimeout(function () {
            var f = cy.getElementById(focusId.toString());
            if (f.length) {
                cy.center(f);
                cy.zoom(currentZoom);
            } else {
                cy.fit(null, 80);
                currentZoom = cy.zoom();
            }
            updateSizes();
        }, 50);

    } catch (err) {
        console.error('cytoscape init error:', err);
    }
};

window.updateMemoryGraph = function (data, newFocusId) {
    if (!cy) return;
    focusId = newFocusId;

    // Save current zoom before any modification
    currentZoom = cy.zoom();

    // Build sets of current vs new IDs
    var currentIds = {};
    cy.nodes().forEach(function (n) { currentIds[n.id()] = true; });
    var currentEdgeKeys = {};
    cy.edges().forEach(function (e) { currentEdgeKeys[e.data('source') + '||' + e.data('target')] = true; });

    var newIds = {};
    var newEdgeKeys = {};
    var toAdd = [];

    for (var i = 0; i < data.nodes.length; i++) {
        var n = data.nodes[i];
        var id = n.id.toString();
        newIds[id] = n;
        if (!currentIds[id]) {
            var el = { group: 'nodes', data: { id: id, label: n.label || '', title: n.title || '',
                type: n.type || 'fact', importance: n.importance || 0.5,
                isDerived: n.isDerived || false, isCore: n.isCore || false,
                isFocus: n.isFocus || false } };
            if (n.x !== undefined && n.y !== undefined) {
                el.position = { x: n.x, y: n.y };
            }
            el.classes = 'fade-in';
            toAdd.push(el);
        } else {
            // Update existing node data (focus/core status may have changed)
            var existing = cy.getElementById(id);
            if (existing.length) {
                existing.data('isCore', n.isCore || false);
                existing.data('isFocus', n.isFocus || false);
            }
        }
    }

    for (var j = 0; j < data.edges.length; j++) {
        var e = data.edges[j];
        var key = e.from.toString() + '||' + e.to.toString();
        newEdgeKeys[key] = true;
        if (!currentEdgeKeys[key]) {
            toAdd.push({ group: 'edges', data: {
                id: e.from + '-' + e.to, source: e.from.toString(), target: e.to.toString(),
                label: e.label || '', strength: e.width || 0.5
            }});
        }
    }

    // Remove nodes no longer visible (batch for performance)
    var toRemove = cy.nodes().filter(function (n) { return !newIds[n.id()]; });
    // Also remove stale edges
    var staleEdges = cy.edges().filter(function (e) {
        return !newEdgeKeys[e.data('source') + '||' + e.data('target')];
    });

    if (toRemove.length > 0 || staleEdges.length > 0) {
        cy.batch(function () {
            staleEdges.remove();
            toRemove.remove();
        });
    }

    // Add new elements
    if (toAdd.length > 0) {
        cy.add(toAdd);
    }

    // Restore zoom (may have been altered by add/remove)
    cy.zoom(currentZoom);
    updateSizes();

    // Animate in new nodes
    setTimeout(function () {
        cy.nodes('.fade-in').removeClass('fade-in');
    }, 300);
};

window.focusOnNode = function (nodeId) {
    if (!cy) return;
    var node = cy.getElementById(nodeId.toString());
    if (!node.length) return;
    highlightAround(node);
    cy.animate({
        fit: { eles: node.closedNeighborhood(), padding: 100 },
        center: { eles: node }, duration: 400, easing: 'ease-in-out'
    });
    currentZoom = cy.zoom();
};

window.resetGraphView = function () {
    if (!cy) return;
    resetHighlight();
    cy.fit(null, 60);
    currentZoom = cy.zoom();
};

window.destroyMemoryGraph = function () {
    if (cy) { cy.destroy(); cy = null; }
    dotNetRef = null;
    focusId = null;
    currentZoom = 0.8;
};

// --- internal ---

function setupZoomCompensation() {
    var targetNodePx = 14;
    var targetFontPx = 13;
    var targetEdgeWidth = 1.5;
    var targetEdgeFont = 9;

    window.updateSizes = function () {
        if (!cy) return;
        var z = cy.zoom();
        if (z <= 0) return;
        var bw = 1.2 / z;
        cy.style().selector('node').style({
            'width': targetNodePx * 2 / z, 'height': targetNodePx * 2 / z,
            'font-size': targetFontPx / z, 'border-width': bw
        }).selector('node[?isCore]').style({
            'border-width': bw * 2.5, 'border-color': '#ffd54f'
        }).selector('node[?isFocus]').style({
            'border-width': bw * 2.5, 'border-color': '#64ffda'
        }).selector('node:selected').style({
            'border-width': bw * 2.5, 'border-color': '#ffd54f'
        }).selector('node.highlight').style({
            'border-width': bw * 2, 'shadow-blur': 8 / z
        }).selector('edge').style({
            'width': targetEdgeWidth / z, 'font-size': targetEdgeFont / z
        }).selector('edge.highlight').style({
            'width': targetEdgeWidth * 2 / z
        }).update();
        currentZoom = z;
    };

    cy.on('zoom', updateSizes);
}

function setupEvents(container) {
    // Click node → notify C# (C# handles focus change + re-filter)
    cy.on('tap', 'node', function (evt) {
        var node = evt.target;
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnNodeSelected', parseInt(node.id()));
        }
    });

    cy.on('tap', function (evt) {
        if (evt.target === cy) {
            resetHighlight();
            if (dotNetRef) { dotNetRef.invokeMethodAsync('OnCanvasClicked'); }
        }
    });

    cy.on('mouseover', 'node', function (evt) {
        highlightAround(evt.target);
        container.style.cursor = 'pointer';
    });

    cy.on('mouseout', 'node', function () {
        resetHighlight();
        container.style.cursor = 'default';
    });
}

function buildElements(nodes, edges) {
    var els = [];
    var nodeIndex = {};
    for (var i = 0; i < nodes.length; i++) {
        var n = nodes[i];
        var id = n.id.toString();
        nodeIndex[id] = n;
        var el = {
            group: 'nodes',
            data: {
                id: id, label: n.label || '', title: n.title || '',
                type: n.type || 'fact', importance: n.importance || 0.5,
                isDerived: n.isDerived || false, isCore: n.isCore || false,
                isFocus: n.isFocus || false
            }
        };
        if (n.x !== undefined && n.y !== undefined) {
            el.position = { x: n.x, y: n.y };
        }
        els.push(el);
    }
    for (var j = 0; j < edges.length; j++) {
        var e = edges[j];
        var src = e.from.toString(), tgt = e.to.toString();
        if (!nodeIndex[src] || !nodeIndex[tgt]) continue;
        els.push({
            group: 'edges',
            data: {
                id: src + '-' + tgt, source: src, target: tgt,
                label: e.label || '', strength: e.width || 0.5
            }
        });
    }
    return els;
}

function makeStyles() {
    return [
        { selector: 'node', style: {
            'background-color': '#90a4ae', 'border-color': '#555',
            'opacity': 0.95, 'label': 'data(label)', 'color': '#e0e0e0',
            'text-valign': 'center', 'text-halign': 'center',
            'text-wrap': 'wrap', 'text-max-width': '200px',
            'text-overflow-wrap': 'anywhere'
        }},
        { selector: 'node[?isCore]', style: { 'border-color': '#ffd54f', 'font-weight': 'bold' }},
        { selector: 'node[?isFocus]', style: { 'border-color': '#64ffda', 'border-width': 3 }},
        { selector: 'node[?isDerived]', style: { 'border-style': 'dashed' }},
        { selector: 'node.fade-in', style: { 'opacity': 0, 'transition-property': 'opacity', 'transition-duration': 300 }},
        { selector: 'node[type="knowledge"]', style: { 'background-color': '#4fc3f7' }},
        { selector: 'node[type="fact"]', style: { 'background-color': '#81c784' }},
        { selector: 'node[type="feedback"]', style: { 'background-color': '#ffb74d' }},
        { selector: 'node[type="inference"]', style: { 'background-color': '#ce93d8' }},
        { selector: 'node[type="event"]', style: { 'background-color': '#f06292' }},
        { selector: 'node:selected', style: { 'border-color': '#ffd54f' }},
        { selector: 'node.highlight', style: { 'opacity': 1, 'shadow-color': '#2196f3', 'shadow-opacity': 0.5 }},
        { selector: 'node.dimmed', style: { 'opacity': 0.2 }},
        { selector: 'edge', style: {
            'line-color': '#556', 'opacity': 0.35, 'curve-style': 'bezier',
            'label': 'data(label)', 'color': '#666'
        }},
        { selector: 'edge.highlight', style: { 'opacity': 0.8 }},
        { selector: 'edge.dimmed', style: { 'opacity': 0.05 }}
    ];
}

function highlightAround(node) {
    if (!cy || !node.length) return;
    var hood = node.closedNeighborhood();
    cy.batch(function () {
        cy.nodes().addClass('dimmed');
        cy.edges().addClass('dimmed');
        hood.nodes().removeClass('dimmed').addClass('highlight');
        hood.edges().removeClass('dimmed').addClass('highlight');
    });
}

function resetHighlight() {
    if (!cy) return;
    cy.nodes().removeClass('dimmed highlight');
    cy.edges().removeClass('dimmed highlight');
}
