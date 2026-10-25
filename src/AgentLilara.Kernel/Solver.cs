namespace AgentLilara.Kernel.Graph;

/// <summary>一次求解的结果。Success=false 即"求解无效"：本拍不产生任何副作用。</summary>
public sealed record SolveResult(bool Success, IReadOnlyList<string> Unresolved, EvalContext Context);

/// <summary>
/// 求解器：初始除激活源外全部输出悬空；按"输入全部确定才就绪"逐个求解，每点只写一次。
/// 队列空而仍有节点未知 → 求解无效（天然拦住同拍依赖环）。
/// </summary>
public static class Solver
{
    public static SolveResult Solve(GraphClass graph, EvalContext context)
    {
        Validate(graph);

        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var orderIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            orderIndex[graph.Nodes[i].Id] = i;
        }

        // 输入口 → 产出它的输出口
        var producer = new Dictionary<(string Node, string Port), (string FromNode, string FromPort)>();
        foreach (var link in graph.Links)
        {
            producer[(link.ToNode, link.ToPort)] = (link.FromNode, link.FromPort);
        }

        var cells = new Dictionary<string, Dictionary<string, Cell>>(StringComparer.Ordinal);
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        var boundCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var link in graph.Links)
        {
            boundCount[link.ToNode] = boundCount.TryGetValue(link.ToNode, out var count) ? count + 1 : 1;
        }

        foreach (var node in graph.Nodes)
        {
            // 就绪 = 已接线的输入口全部就位；未接线的可选输入口不参与（脚本自己取默认值）
            remaining[node.Id] = boundCount.TryGetValue(node.Id, out var bound) ? bound : 0;
            cells[node.Id] = new Dictionary<string, Cell>(StringComparer.Ordinal);
        }

        // 就绪集合按"声明顺序 → id 字典序"出队，保证同一输入得到同一求值序列
        var ready = new SortedSet<(int Index, string Id)>(
            Comparer<(int Index, string Id)>.Create((a, b) =>
                a.Index != b.Index ? a.Index.CompareTo(b.Index) : string.CompareOrdinal(a.Id, b.Id)));

        foreach (var node in graph.Nodes)
        {
            if (remaining[node.Id] == 0)
            {
                ready.Add((orderIndex[node.Id], node.Id));
            }
        }

        while (ready.Count > 0)
        {
            var (_, id) = ready.Min;
            ready.Remove(ready.Min);

            var node = nodes[id];
            var inputs = new Dictionary<string, Cell>(StringComparer.Ordinal);
            foreach (var port in node.InputPorts)
            {
                if (!producer.TryGetValue((id, port), out var source))
                {
                    continue; // 未接线的可选输入口：交给节点按声明默认值处理
                }

                inputs[port] = cells[source.FromNode][source.FromPort];
            }

            var outputs = node.Evaluate(inputs, context);
            var bag = new Dictionary<string, Cell>(StringComparer.Ordinal);
            foreach (var port in node.OutputPorts)
            {
                // 未声明的输出口一律视为悬空（节点自身负责写满）
                bag[port] = outputs.TryGetValue(port, out var cell) ? cell : Cell.Unknown;
            }

            cells[id] = bag;
            context.EvaluationOrder.Add(id);

            foreach (var link in graph.Links)
            {
                if (!string.Equals(link.FromNode, id, StringComparison.Ordinal))
                {
                    continue;
                }

                remaining[link.ToNode]--;
                if (remaining[link.ToNode] == 0)
                {
                    ready.Add((orderIndex[link.ToNode], link.ToNode));
                }
            }
        }

        var unresolved = remaining
            .Where(kv => kv.Value > 0)
            .Select(kv => kv.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return new SolveResult(unresolved.Count == 0, unresolved, context);
    }

    /// <summary>静态校验：唯一激活源、输入绑定、端点合法、汇块无下游。</summary>
    public static void Validate(GraphClass graph)
    {
        if (graph.Nodes.Count == 0)
        {
            throw new GraphValidationException($"graph '{graph.ClassId}' has no nodes");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (!ids.Add(node.Id))
            {
                throw new GraphValidationException($"duplicate node id '{node.Id}'");
            }
        }

        var sources = graph.Nodes.Where(n => n.IsActivationSource).ToList();
        if (sources.Count != 1)
        {
            throw new GraphValidationException(
                $"graph '{graph.ClassId}' must have exactly one activation source, found {sources.Count}");
        }

        foreach (var node in graph.Nodes.Where(n => n.IsActivationSource))
        {
            if (node.Kind != NodeKind.Source)
            {
                throw new GraphValidationException($"activation source '{node.Id}' must be a source node");
            }

            if (node.InputPorts.Count != 0)
            {
                throw new GraphValidationException($"activation source '{node.Id}' must not have inputs");
            }
        }

        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var bound = new HashSet<(string Node, string Port)>();
        foreach (var link in graph.Links)
        {
            if (!nodes.TryGetValue(link.FromNode, out var from))
            {
                throw new GraphValidationException($"link references unknown node '{link.FromNode}'");
            }

            if (!nodes.TryGetValue(link.ToNode, out var to))
            {
                throw new GraphValidationException($"link references unknown node '{link.ToNode}'");
            }

            if (!from.OutputPorts.Contains(link.FromPort, StringComparer.Ordinal))
            {
                throw new GraphValidationException($"node '{from.Id}' has no output port '{link.FromPort}'");
            }

            if (!to.InputPorts.Contains(link.ToPort, StringComparer.Ordinal))
            {
                throw new GraphValidationException($"node '{to.Id}' has no input port '{link.ToPort}'");
            }

            if (!bound.Add((link.ToNode, link.ToPort)))
            {
                throw new GraphValidationException($"input '{link.ToNode}.{link.ToPort}' bound more than once");
            }

            if (from.Kind == NodeKind.Sink)
            {
                throw new GraphValidationException($"sink node '{from.Id}' must not have downstream links");
            }
        }

        foreach (var node in graph.Nodes)
        {
            var optional = node is IOptionalInputs withOptional
                ? withOptional.OptionalInputPorts
                : Array.Empty<string>();

            foreach (var port in node.InputPorts)
            {
                if (bound.Contains((node.Id, port)) || optional.Contains(port, StringComparer.Ordinal))
                {
                    continue;
                }

                throw new GraphValidationException($"input '{node.Id}.{port}' is unbound");
            }
        }
    }
}
