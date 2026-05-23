var cy = null;
var dotNetRef = null;
var coreNodeId = null;
var typeColor = {
    'knowledge': '#4fc3f7', 'fact': '#81c784', 'feedback': '#ffb74d',
    'inference': '#ce93d8', 'event': '#f06292'
};
var edgeColor = {
    '共现': '#4fc3f7', '时序': '#81c784', '语义': '#ffb74d', '因果': '#e57373'
};

window.initMemoryGraph = function (containerId, data, coreId, dotnet) {
    dotNetRef = dotnet;
    coreNodeId = coreId;

    var container = document.getElementById(containerId);
    if (!container) return;

    // Clean up previous instance
    if (cy) { cy.destroy(); cy = null; }

    // Build elements (data-only, no inline styles)
    var els = [];
    var addedEdges = {};
    var nodeIndex = {};

    // Nodes
    for (var i = 0; i < data.nodes.length; i++) {
        var n = data.nodes[i];
        var id = n.id.toString();
        nodeIndex[id] = n;
        els.push({
            group: 'nodes',
            data: {
                id: id,
                label: n.label || '',
                title: n.title || '',
                type: n.type || 'fact',
                importance: n.importance || 0.5,
                isDerived: n.isDerived || false,
                isCore: (id === coreId.toString())
            }
        });
    }

    // Edges (deduplicate by source-target pair)
    for (var j = 0; j < data.edges.length; j++) {
        var e = data.edges[j];
        var src = e.from.toString();
        var tgt = e.to.toString();

        // Guard: skip edges referencing non-existent nodes
        if (!nodeIndex[src] || !nodeIndex[tgt]) continue;

        var key = src + '||' + tgt;
        if (addedEdges[key]) {
            // For parallel edges, append index to id
            var cnt = addedEdges[key];
            addedEdges[key] = cnt + 1;
            els.push({
                group: 'edges',
                data: {
                    id: src + '-' + tgt + '-' + cnt,
                    source: src, target: tgt,
                    label: e.label || '',
                    strength: e.width || 0.5,
                    linkType: e.label || ''
                }
            });
        } else {
            addedEdges[key] = 1;
            els.push({
                group: 'edges',
                data: {
                    id: src + '-' + tgt,
                    source: src, target: tgt,
                    label: e.label || '',
                    strength: e.width || 0.5,
                    linkType: e.label || ''
                }
            });
        }
    }

    // Create cytoscape instance
    try {
        cy = cytoscape({
            container: container,
            elements: els,
            style: [
                // --- nodes ---
                {
                    selector: 'node',
                    style: {
                        'background-color': '#90a4ae',
                        'width': 'mapData(importance, 0, 1, 12, 32)',
                        'height': 'mapData(importance, 0, 1, 12, 32)',
                        'border-width': 1.5,
                        'border-color': '#555',
                        'opacity': 0.9,
                        'label': 'data(label)',
                        'font-size': 10,
                        'color': '#e0e0e0',
                        'text-valign': 'bottom',
                        'text-halign': 'center',
                        'text-margin-y': 6,
                        'text-wrap': 'ellipsis',
                        'text-max-width': '120px',
                        'transition-property': 'width, height, border-color, opacity',
                        'transition-duration': 300
                    }
                },
                {
                    selector: 'node[?isCore]',
                    style: {
                        'border-color': '#ffd54f',
                        'border-width': 3,
                        'font-size': 13,
                        'font-weight': 'bold'
                    }
                },
                {
                    selector: 'node[?isDerived]',
                    style: { 'border-style': 'dashed' }
                },
                {
                    selector: 'node[type="knowledge"]', style: { 'background-color': '#4fc3f7' }
                },
                {
                    selector: 'node[type="fact"]', style: { 'background-color': '#81c784' }
                },
                {
                    selector: 'node[type="feedback"]', style: { 'background-color': '#ffb74d' }
                },
                {
                    selector: 'node[type="inference"]', style: { 'background-color': '#ce93d8' }
                },
                {
                    selector: 'node[type="event"]', style: { 'background-color': '#f06292' }
                },
                {
                    selector: 'node:selected',
                    style: {
                        'border-color': '#ffd54f',
                        'border-width': 3,
                        'shadow-color': '#ffd54f',
                        'shadow-blur': 12,
                        'shadow-opacity': 0.6
                    }
                },
                {
                    selector: 'node.highlight',
                    style: {
                        'opacity': 1,
                        'shadow-color': '#2196f3',
                        'shadow-blur': 10,
                        'shadow-opacity': 0.5
                    }
                },
                {
                    selector: 'node.dimmed',
                    style: { 'opacity': 0.2 }
                },

                // --- edges ---
                {
                    selector: 'edge',
                    style: {
                        'width': 1.5,
                        'line-color': '#666',
                        'target-arrow-color': '#666',
                        'target-arrow-shape': 'triangle',
                        'arrow-scale': 0.8,
                        'opacity': 0.4,
                        'curve-style': 'bezier',
                        'label': 'data(label)',
                        'font-size': 9,
                        'color': '#888',
                        'edge-text-rotation': 'autorotate',
                        'transition-property': 'width, opacity',
                        'transition-duration': 300
                    }
                },
                {
                    selector: 'edge[linkType="共现"]', style: { 'line-color': '#4fc3f7', 'target-arrow-color': '#4fc3f7' }
                },
                {
                    selector: 'edge[linkType="时序"]', style: { 'line-color': '#81c784', 'target-arrow-color': '#81c784' }
                },
                {
                    selector: 'edge[linkType="语义"]', style: { 'line-color': '#ffb74d', 'target-arrow-color': '#ffb74d' }
                },
                {
                    selector: 'edge[linkType="因果"]', style: { 'line-color': '#e57373', 'target-arrow-color': '#e57373' }
                },
                {
                    selector: 'edge.highlight',
                    style: { 'opacity': 0.8, 'width': 3 }
                },
                {
                    selector: 'edge.dimmed',
                    style: { 'opacity': 0.05 }
                }
            ],
            layout: {
                name: 'cose',
                animate: false,
                nodeRepulsion: function (node) { return node.data('isCore') ? 20000 : 4000; },
                gravity: 80,
                numIter: Math.min(500, els.length * 2),
                coolingFactor: 0.99,
                fit: true,
                padding: 60
            },
            wheelSensitivity: 0.3,
            // Performance: skip rendering during layout for large graphs
            motionBlur: false,
            pixelRatio: 1
        });

        // Show progress for large graphs
        if (els.length > 200) {
            try {
                var loading = document.getElementById('graph-loading');
                if (loading) loading.style.display = 'block';
                cy.one('layoutstop', function () {
                    if (loading) loading.style.display = 'none';
                });
            } catch (_) {}
        }

        // --- events ---

        cy.on('tap', 'node', function (evt) {
            var node = evt.target;
            panToNode(node);
            highlightNeighbors(node);
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
            highlightNeighbors(evt.target);
            container.style.cursor = 'pointer';
        });

        cy.on('mouseout', 'node', function () {
            resetHighlight();
            container.style.cursor = 'default';
        });

        // Core pulse after layout
        cy.one('layoutstop', function () {
            setTimeout(function () {
                var core = cy.getElementById(coreId.toString());
                if (core.length) {
                    panToNode(core);
                }
            }, 300);
        });

    } catch (err) {
        console.error('cytoscape init error:', err);
    }
};

// --- public helpers ---

window.focusOnNode = function (nodeId) {
    if (!cy) return;
    var node = cy.getElementById(nodeId.toString());
    if (!node.length) return;
    panToNode(node);
    resetHighlight();
    highlightNeighbors(node);
};

window.resetGraphView = function () {
    if (!cy) return;
    resetHighlight();
    cy.fit(null, 60);
};

window.destroyMemoryGraph = function () {
    if (cy) { cy.destroy(); cy = null; }
    dotNetRef = null;
    coreNodeId = null;
};

// --- internal ---

function panToNode(node) {
    if (!cy || !node.length) return;
    cy.animate({
        fit: { eles: node.closedNeighborhood(), padding: 100 },
        center: { eles: node },
        duration: 500,
        easing: 'ease-in-out'
    });
}

function highlightNeighbors(node) {
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
