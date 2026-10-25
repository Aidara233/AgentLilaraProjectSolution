using System.Text.Json;
using AgentLilara.GraphRuntime.Graph;

namespace AgentLilara.GraphRuntime.Serialization;

/// <summary>把图源码（JSON）加载成 <see cref="GraphClass"/> 并做编译期校验。</summary>
public static class GraphLoader
{
    public static GraphDocument Parse(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<GraphDocument>(json, GraphJson.Options);
            return document ?? throw new GraphLoadException("document is empty");
        }
        catch (JsonException ex)
        {
            throw new GraphLoadException($"invalid json: {ex.Message}");
        }
    }

    public static GraphClass Load(string json) => Load(Parse(json));

    public static GraphClass Load(string json, INodeFactory factory) => Load(Parse(json), factory);

    public static GraphClass Load(GraphDocument document) => Load(document, NodeRegistry.Default);

    public static GraphClass Load(GraphDocument document, INodeFactory factory)
    {
        if (string.IsNullOrWhiteSpace(document.ClassId))
        {
            throw new GraphLoadException("classId is required");
        }

        var kind = document.Kind?.Trim().ToLowerInvariant() switch
        {
            "static" => GraphKind.Static,
            "dynamic" => GraphKind.Dynamic,
            null or "" => throw new GraphLoadException($"graph '{document.ClassId}': kind is required"),
            _ => throw new GraphLoadException($"graph '{document.ClassId}': unknown kind '{document.Kind}'"),
        };

        var nodes = new List<INode>(document.Nodes.Count);
        foreach (var node in document.Nodes)
        {
            nodes.Add(factory.Create(node.Id, node.Type, node.Params));
        }

        var links = new List<Link>(document.Links.Count);
        foreach (var link in document.Links)
        {
            var (fromNode, fromPort) = SplitEndpoint(link.From, "from");
            var (toNode, toPort) = SplitEndpoint(link.To, "to");
            links.Add(new Link(fromNode, fromPort, toNode, toPort));
        }

        var variables = new List<VariableDecl>(document.Variables.Count);
        foreach (var variable in document.Variables)
        {
            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                throw new GraphLoadException($"graph '{document.ClassId}': variable name is required");
            }

            variables.Add(new VariableDecl(
                variable.Name,
                ToCell(variable.Type, variable.Initial, variable.Name),
                variable.Mutable,
                variable.Description));
        }

        var graph = new GraphClass(document.ClassId, kind, nodes, links, variables);

        // 编译期校验：唯一激活源、输入绑定、端点合法、汇块无下游
        Solver.Validate(graph);
        return graph;
    }

    private static (string Node, string Port) SplitEndpoint(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new GraphLoadException($"link '{field}' is empty");
        }

        var index = value.LastIndexOf('.');
        if (index <= 0 || index == value.Length - 1)
        {
            throw new GraphLoadException($"link '{field}' must be 'node.port', got '{value}'");
        }

        return (value[..index], value[(index + 1)..]);
    }

    private static Cell ToCell(string type, JsonElement initial, string variableName)
    {
        var normalized = (type ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            normalized = "int";
        }

        if (initial.ValueKind == JsonValueKind.Undefined)
        {
            throw new GraphLoadException($"variable '{variableName}': initial value is required");
        }

        switch (normalized)
        {
            case "int":
                if (initial.ValueKind == JsonValueKind.Number && initial.TryGetInt32(out var i))
                {
                    return Cell.Of(i);
                }

                throw new GraphLoadException($"variable '{variableName}': expected int initial value");

            case "bool":
                return initial.ValueKind switch
                {
                    JsonValueKind.True => Cell.Of(true),
                    JsonValueKind.False => Cell.Of(false),
                    _ => throw new GraphLoadException($"variable '{variableName}': expected bool initial value"),
                };

            case "string":
                if (initial.ValueKind == JsonValueKind.String)
                {
                    return Cell.Of(initial.GetString() ?? string.Empty);
                }

                throw new GraphLoadException($"variable '{variableName}': expected string initial value");

            default:
                throw new GraphLoadException($"variable '{variableName}': unknown type '{type}'");
        }
    }
}
