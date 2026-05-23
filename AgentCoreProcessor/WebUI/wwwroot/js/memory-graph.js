let cy = null;
let dotNetRef = null;
let coreNodeId = null;
let colorMap = {
    'knowledge': '#4fc3f7',
    'fact': '#81c784',
    'feedback': '#ffb74d',
    'inference': '#ce93d8',
    'event': '#f06292'
};

window.initMemoryGraph = function (containerId, data, coreId, dotnet) {
    dotNetRef = dotnet;
    coreNodeId = coreId;

    var container = document.getElementById(containerId);
    if (!container) return;

    if (cy) cy.destroy();

    var maxImportance = 0;
    var nodes = data.nodes.map(function (n) {
        if (n.importance > maxImportance) maxImportance = n.importance;
        return {
            data: {
                id: n.id.toString(),
                label: n.label,
                title: n.title,
                type: n.type,
                importance: n.importance || 0.5,
                isDerived: n.isDerived || false,
                isCore: (n.id.toString() === coreId.toString())
            }
        };
    });

    var elements = [];

    nodes.forEach(function (n) {
        var color = colorMap[n.data.type] || '#90a4ae';
        var size = 12 + (n.data.importance / Math.max(maxImportance, 1)) * 20;
        if (n.data.isCore) size += 10;

        elements.push({
            data: n.data,
            style: {
                'background-color': color,
                'width': size,
                'height': size,
                'border-width': n.data.isCore ? 3 : 1.5,
                'border-color': n.data.isCore ? '#ffd54f' : '#555',
                'border-style': n.data.isDerived ? 'dashed' : 'solid',
                'opacity': 0.9
            },
            selected: n.data.isCore
        });
    });

    var edges = data.edges.map(function (e) {
        var typeColors = {
            '共现': '#4fc3f7',
            '时序': '#81c784',
            '语义': '#ffb74d',
            '因果': '#e57373'
        };
        var eColor = typeColors[e.label] || '#666';
        var width = 0.5 + (e.width || 1) * 3;
        return {
            data: {
                id: e.from + '-' + e.to,
                source: e.from.toString(),
                target: e.to.toString(),
                label: e.label || '',
                strength: e.width || 0.5
            },
            style: {
                'width': width,
                'line-color': eColor,
                'target-arrow-color': eColor,
                'target-arrow-shape': 'triangle',
                'arrow-scale': 0.6 + (e.width || 0.5) * 0.5,
                'opacity': 0.4,
                'curve-style': 'bezier'
            }
        };
    });

    elements = elements.concat(edges);

    cy = cytoscape({
        container: container,
        elements: elements,
        style: [
            {
                selector: 'node',
                style: {
                    'label': 'data(label)',
                    'font-size': function (ele) { return ele.data('isCore') ? 13 : 10; },
                    'font-weight': function (ele) { return ele.data('isCore') ? 'bold' : 'normal'; },
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
                selector: 'edge',
                style: {
                    'label': 'data(label)',
                    'font-size': 9,
                    'color': '#888',
                    'edge-text-rotation': 'autorotate',
                    'transition-property': 'width, opacity',
                    'transition-duration': 300
                }
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
            {
                selector: 'edge.highlight',
                style: { 'opacity': 0.8, 'width': 3 }
            },
            {
                selector: 'edge.dimmed',
                style: { 'opacity': 0.05 }
            },
            {
                selector: 'node.animated',
                style: {
                    'border-width': 4,
                    'border-color': '#fff',
                    'border-opacity': 0
                }
            }
        ],
        layout: {
            name: 'cose',
            animate: true,
            animationDuration: 800,
            animationEasing: 'ease-in-out',
            idealEdgeLength: function (edge) {
                var s = edge.data('strength') || 0.5;
                return (1 - s) * 200 + 40;
            },
            edgeElasticity: function (edge) {
                var s = edge.data('strength') || 0.5;
                return s * 0.8 + 0.1;
            },
            nodeRepulsion: 8000,
            gravity: 20,
            numIter: 2000,
            coolingFactor: 0.95,
            initialTemp: 100,
            fit: true,
            padding: 60,
            randomize: true
        },
        interaction: {
            hoverDelay: 50
        },
        wheelSensitivity: 0.3
    });

    // --- events ---

    cy.on('tap', 'node', function (evt) {
        var node = evt.target;
        focusOnNode(node);

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
        var node = evt.target;
        highlightNeighbors(node);
        container.style.cursor = 'pointer';
    });

    cy.on('mouseover', 'edge', function (evt) {
        var edge = evt.target;
        edge.style({ 'opacity': 1, 'width': edge.style('width') * 1.5 });
        edge.source().style({ 'border-color': '#fff', 'border-width': 2 });
        edge.target().style({ 'border-color': '#fff', 'border-width': 2 });
    });

    cy.on('mouseout', 'edge', function (evt) {
        var edge = evt.target;
        edge.style({ 'opacity': 0.4, 'width': edge.data('strength') * 3 + 0.5 });
        edge.source().style({ 'border-color': '#555', 'border-width': 1.5 });
        edge.target().style({ 'border-color': '#555', 'border-width': 1.5 });
    });

    cy.on('mouseout', 'node', function () {
        resetHighlight();
        container.style.cursor = 'default';
    });

    // After layout settles, pulse the core node
    cy.on('layoutstop', function () {
        var core = cy.getElementById(coreId.toString());
        if (core.length) {
            core.addClass('animated');
            animatePulse(core);
        }
    });

    // Initial focus on core after layout
    setTimeout(function () {
        var core = cy.getElementById(coreId.toString());
        if (core.length) {
            cy.animate({
                fit: { eles: core.closedNeighborhood(), padding: 80 },
                center: { eles: core },
                duration: 1000,
                easing: 'ease-in-out'
            });
        }
    }, 1200);
};

window.focusOnNode = function (nodeId) {
    if (!cy) return;
    var node = cy.getElementById(nodeId.toString());
    if (!node.length) return;

    cy.nodes().removeClass('animated');
    node.addClass('animated');
    animatePulse(node);

    cy.animate({
        fit: { eles: node.closedNeighborhood().union(node), padding: 100 },
        center: { eles: node },
        duration: 600,
        easing: 'ease-in-out'
    });

    resetHighlight();
    highlightNeighbors(node);
    node.style({ 'border-color': '#ffd54f', 'border-width': 3 });
};

window.highlightNeighbors = function (node) {
    // Alias for external use
    if (!cy) return;
    highlightNeighbors(node);
};

window.resetGraphView = function () {
    if (!cy) return;
    resetHighlight();
    cy.fit(null, 60);
};

window.destroyMemoryGraph = function () {
    if (cy) {
        cy.destroy();
        cy = null;
    }
    dotNetRef = null;
    coreNodeId = null;
};

// --- internal helpers ---

function highlightNeighbors(node) {
    if (!cy) return;
    var neighborhood = node.closedNeighborhood();
    cy.nodes().addClass('dimmed');
    cy.edges().addClass('dimmed');
    neighborhood.nodes().removeClass('dimmed').addClass('highlight');
    neighborhood.edges().removeClass('dimmed').addClass('highlight');
}

function resetHighlight() {
    if (!cy) return;
    cy.nodes().removeClass('dimmed highlight');
    cy.edges().removeClass('dimmed highlight');
}

function focusOnNode(node) {
    if (!cy) return;

    cy.nodes().removeClass('animated');
    node.addClass('animated');
    animatePulse(node);

    cy.animate({
        fit: { eles: node.closedNeighborhood().union(node), padding: 100 },
        center: { eles: node },
        duration: 600,
        easing: 'ease-in-out'
    });

    resetHighlight();
    highlightNeighbors(node);
}

function animatePulse(node) {
    var step = 0;
    var totalSteps = 12;
    var interval = setInterval(function () {
        if (!cy || cy.destroyed || !node.length) { clearInterval(interval); return; }
        if (step >= totalSteps) {
            node.style({
                'border-opacity': 0,
                'shadow-opacity': 0.6,
                'shadow-color': '#ffd54f',
                'shadow-blur': 8
            });
            clearInterval(interval);
            return;
        }
        var brightness = Math.sin(step / totalSteps * Math.PI) * 0.8 + 0.2;
        node.style({
            'border-opacity': brightness,
            'shadow-opacity': brightness * 0.8,
            'shadow-color': '#ffd54f',
            'shadow-blur': 8 + brightness * 10,
            'border-color': step % 2 === 0 ? '#fff' : '#ffd54f',
            'border-width': 2 + brightness * 2
        });
        step++;
    }, 80);
}
