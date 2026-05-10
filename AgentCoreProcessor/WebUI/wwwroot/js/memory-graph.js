var memoryGraphInstance = null;
var memoryGraphDotNetRef = null;

window.initMemoryGraph = function (containerId, data, dotNetRef) {
    memoryGraphDotNetRef = dotNetRef;
    var container = document.getElementById(containerId);
    if (!container) return;

    var colorMap = {
        'knowledge': '#4fc3f7',
        'fact': '#81c784',
        'feedback': '#ffb74d',
        'inference': '#ce93d8',
        'event': '#f06292'
    };

    var nodes = data.nodes.map(function (n) {
        return {
            id: n.id,
            label: n.label,
            title: n.title,
            color: {
                background: colorMap[n.group] || '#90a4ae',
                border: '#555',
                highlight: { background: '#d4af37', border: '#d4af37' }
            },
            font: { color: '#e0e0e0', size: 11 },
            shape: 'dot',
            size: 8 + (n.importance || 0.5) * 12
        };
    });

    var edges = data.edges.map(function (e) {
        return {
            from: e.from,
            to: e.to,
            label: e.label,
            width: e.width,
            color: { color: '#666', highlight: '#d4af37' },
            font: { color: '#888', size: 9, strokeWidth: 0 },
            smooth: { type: 'continuous' }
        };
    });

    var options = {
        physics: {
            solver: 'forceAtlas2Based',
            forceAtlas2Based: { gravitationalConstant: -30, springLength: 100 },
            stabilization: { iterations: 150 }
        },
        interaction: {
            hover: true,
            tooltipDelay: 100,
            navigationButtons: false
        },
        nodes: { borderWidth: 1 },
        edges: { smooth: { type: 'continuous' } }
    };

    memoryGraphInstance = new vis.Network(container, { nodes: nodes, edges: edges }, options);

    memoryGraphInstance.on('click', function (params) {
        if (params.nodes.length > 0 && memoryGraphDotNetRef) {
            memoryGraphDotNetRef.invokeMethodAsync('OnNodeClicked', params.nodes[0]);
        }
    });
};

window.destroyMemoryGraph = function () {
    if (memoryGraphInstance) {
        memoryGraphInstance.destroy();
        memoryGraphInstance = null;
    }
    memoryGraphDotNetRef = null;
};
