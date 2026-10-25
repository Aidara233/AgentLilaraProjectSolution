using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentLilara.Kernel.Serialization;

/// <summary>图源码文件（JSON）的传输形态。字段与 `docs/contracts/13-graph-json.md` 一致。</summary>
public sealed class GraphDocument
{
    public string ClassId { get; set; } = string.Empty;

    /// <summary>static | dynamic</summary>
    public string Kind { get; set; } = "static";

    public int Version { get; set; } = 1;

    public List<VariableDocument> Variables { get; set; } = new();

    public List<NodeDocument> Nodes { get; set; } = new();

    public List<LinkDocument> Links { get; set; } = new();
}

public sealed class VariableDocument
{
    public string Name { get; set; } = string.Empty;

    /// <summary>int | bool | string</summary>
    public string Type { get; set; } = "int";

    public JsonElement Initial { get; set; }

    public bool Mutable { get; set; } = true;

    public string? Description { get; set; }
}

public sealed class NodeDocument
{
    public string Id { get; set; } = string.Empty;

    /// <summary>节点类型，见 <see cref="NodeRegistry"/>。</summary>
    public string Type { get; set; } = string.Empty;

    public Dictionary<string, JsonElement>? Params { get; set; }

    /// <summary>编辑器坐标，运行期不使用。</summary>
    public double X { get; set; }

    public double Y { get; set; }
}

public sealed class LinkDocument
{
    /// <summary>"nodeId.portName"</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>"nodeId.portName"</summary>
    public string To { get; set; } = string.Empty;
}

internal static class GraphJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}
