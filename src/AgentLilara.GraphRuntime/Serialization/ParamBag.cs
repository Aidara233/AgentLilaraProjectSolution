using System.Text.Json;
using AgentLilara.GraphRuntime.Graph;

namespace AgentLilara.GraphRuntime.Serialization;

/// <summary>图源码加载失败。</summary>
public sealed class GraphLoadException : Exception
{
    public GraphLoadException(string message) : base(message)
    {
    }
}

/// <summary>节点参数袋：类型转换 + 未知参数检查（防错拼被静默忽略）。</summary>
public sealed class ParamBag
{
    private readonly string _nodeId;
    private readonly IReadOnlyDictionary<string, JsonElement> _values;

    public ParamBag(string nodeId, IReadOnlyDictionary<string, JsonElement>? values)
    {
        _nodeId = nodeId;
        _values = values ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }

    public string? GetString(string name)
    {
        if (!TryTake(name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw Fail(name, "expected string");
        }

        return element.GetString();
    }

    public int GetInt(string name, int fallback = 0)
    {
        if (!TryTake(name, out var element))
        {
            return fallback;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw Fail(name, "expected integer");
        }

        return value;
    }

    public Cell GetCell(string name, Cell fallback)
    {
        if (!TryTake(name, out var element))
        {
            return fallback;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => Cell.Of(true),
            JsonValueKind.False => Cell.Of(false),
            JsonValueKind.String => Cell.Of(element.GetString() ?? string.Empty),
            JsonValueKind.Number when element.TryGetInt32(out var i) => Cell.Of(i),
            _ => throw Fail(name, "expected bool / int / string"),
        };
    }

    /// <summary>拒绝多余参数：错拼的参数否则会静默落到默认值。</summary>
    public void EnsureOnly(params string[] allowed)
    {
        foreach (var key in _values.Keys)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                throw new GraphLoadException($"node '{_nodeId}': unknown param '{key}'");
            }
        }
    }

    private bool TryTake(string name, out JsonElement element)
    {
        if (_values.TryGetValue(name, out element))
        {
            return true;
        }

        return false;
    }

    private GraphLoadException Fail(string name, string problem) =>
        new($"node '{_nodeId}': param '{name}' {problem}");
}
