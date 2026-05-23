var cy = null;
var dotNetRef = null;
var subDataCache = {};
var isMetaView = false;
var currentViewId = null;

window.initMemoryGraph = function (containerId, data, coreId, dotnet) {
    dotNetRef = dotnet;
    subDataCache = data.subData || {};
    isMetaView = data.isMeta === true;
    currentViewId = coreId;

    var container = document.getElementById(containerId);
    if (!container) return;
    if (cy) { cy.destroy(); cy = null; }

    var els = buildElements(data.nodes, data.edges);

    try {
        cy = cytoscape({
            container: container,
            elements: els,
            style: makeStyles(data.isMeta),
            layout: { name: 'preset', fit: false, padding: 0 },
            wheelSensitivity: 0.3,
            minZoom: 0.02,
            maxZoom: 10
        });

        // Start centered on core at readable zoom
        setTimeout(function () {
            var core = cy.getElementById(coreId.toString());
            if (core.length) {
                cy.center(core);
                cy.zoom(0.5);
            } else {
                cy.fit(null, 80);
            }
        }, 50);

        // Click meta-node to drill in
        cy.on('tap', 'node[?isMeta]', function (evt) {
            var node = evt.target;
            var subId = node.id();
            if (subDataCache[subId]) {
                loadSubGraph(subDataCache[subId]);
            }
        });

        // Click regular node (sub-cluster view)
        cy.on('tap', 'node[!isMeta]', function (evt) {
            var node = evt.target;
            highlightAround(node);
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnNodeSelected', parseInt(node.id()));
            }
        });

        cy.on('tap', function (evt) {
            if (evt.target === cy) {
                resetHighlight();
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnCanvasClicked');
                }
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

    } catch (err) {
        console.error('cytoscape init error:', err);
    }
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
};

window.resetGraphView = function () {
    if (!cy) return;
    resetHighlight();
    cy.fit(null, 60);
};

window.destroyMemoryGraph = function () {
    if (cy) { cy.destroy(); cy = null; }
    dotNetRef = null;
    subDataCache = {};
};

// --- internal ---

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
                isMeta: n.isMeta || false, memberCount: n.memberCount || 0
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

function makeStyles(isMeta) {
    return [
        { selector: 'node', style: {
            'background-color': '#90a4ae', 'border-width': 1, 'border-color': '#555',
            'opacity': 0.95, 'label': 'data(label)', 'font-size': 14, 'color': '#e0e0e0',
            'text-valign': 'center', 'text-halign': 'center',
            'text-wrap': 'wrap', 'text-max-width': '80px',
            'text-overflow-wrap': 'anywhere'
        }},
        { selector: 'node[?isMeta]', style: {
            'border-width': 2, 'font-size': 14, 'font-weight': 'bold',
            'label': 'data(label)', 'text-wrap': 'wrap', 'text-max-width': '120px',
            'color': '#ffd54f', 'text-valign': 'center', 'text-halign': 'center',
            'shape': 'round-rectangle'
        }},
        { selector: 'node[?isCore]', style: { 'border-color': '#ffd54f', 'border-width': 3, 'font-weight': 'bold' }},
        { selector: 'node[?isDerived]', style: { 'border-style': 'dashed' }},
        { selector: 'node[type="knowledge"]', style: { 'background-color': '#4fc3f7' }},
        { selector: 'node[type="fact"]', style: { 'background-color': '#81c784' }},
        { selector: 'node[type="feedback"]', style: { 'background-color': '#ffb74d' }},
        { selector: 'node[type="inference"]', style: { 'background-color': '#ce93d8' }},
        { selector: 'node[type="event"]', style: { 'background-color': '#f06292' }},
        { selector: 'node.highlight', style: { 'opacity': 1, 'shadow-color': '#2196f3', 'shadow-blur': 10, 'shadow-opacity': 0.5 }},
        { selector: 'node.dimmed', style: { 'opacity': 0.2 }},
        { selector: 'edge', style: {
            'width': 1.5, 'line-color': '#556', 'opacity': 0.35, 'curve-style': 'bezier',
            'label': 'data(label)', 'font-size': 9, 'color': '#666'
        }},
        { selector: 'edge.highlight', style: { 'opacity': 0.8, 'width': 3 }},
        { selector: 'edge.dimmed', style: { 'opacity': 0.05 }}
    ];
}

function loadSubGraph(subData) {
    if (!cy) return;
    cy.elements().remove();
    var els = buildElements(subData.nodes, subData.edges);
    cy.add(els);
    cy.layout({ name: 'preset', fit: false, padding: 0 }).run();
    isMetaView = false;
    // Center on sub-cluster core
    setTimeout(function () {
        var core = cy.getElementById(subData.coreId.toString());
        if (core.length) {
            cy.center(core);
            cy.zoom(0.5);
        } else {
            cy.fit(null, 60);
        }
    }, 50);
}

function highlightAround(node) {
    if (!cy || !node.length) return;
    var hood = node.closedNeighborhood();
    cy.nodes().addClass('dimmed');
    cy.edges().addClass('dimmed');
    hood.nodes().removeClass('dimmed').addClass('highlight');
    hood.edges().removeClass('dimmed').addClass('highlight');
}

function resetHighlight() {
    if (!cy) return;
    cy.nodes().removeClass('dimmed highlight');
    cy.edges().removeClass('dimmed highlight');
}
