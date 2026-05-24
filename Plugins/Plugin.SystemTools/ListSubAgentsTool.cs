// Plugins/Plugin.SystemTools/ListSubAgentsTool.cs
using System.Text;
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "列出所有活跃的子agent")]
public class ListSubAgentsTool : ITool
{
    private readonly ISubAgentAccess _subAgents;

    public ListSubAgentsTool(ISubAgentAccess subAgents)
    {
        _subAgents = subAgents;
    }

    public string Name => "list_sub_agents";
    public string Description => "列出所有活跃的子agent及其状态。";

    public IReadOnlyList<ToolParameter> Parameters => new List<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var list = _subAgents.List();
        if (list.Count == 0)
            return Task.FromResult(new ToolResult { Status = "success", Data = "当前无活跃子agent" });

        var sb = new StringBuilder();
        foreach (var info in list)
        {
            var status = info.IsAlive ? "运行中" : "已终止";
            sb.AppendLine($"{info.SessionId} ({status})");
        }
        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = sb.ToString().TrimEnd()
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {},
        "required": []
    }
    """)!;
}
