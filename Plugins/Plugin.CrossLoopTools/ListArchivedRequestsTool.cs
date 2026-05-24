using System.Text;
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "列出已归档的跨循环请求")]
public class ListArchivedRequestsTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public ListArchivedRequestsTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "list_archived_requests";
    public string Description => "列出当前循环可见的所有已归档跨循环请求。";

    public IReadOnlyList<ToolParameter> Parameters => new List<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var archived = _messaging.GetArchivedRequests();
        if (archived.Count == 0)
            return Task.FromResult(new ToolResult { Status = "success", Data = "无已归档请求" });

        var sb = new StringBuilder();
        foreach (var req in archived)
        {
            sb.AppendLine($"#{req.Id[..8]} {req.Title}");
            sb.AppendLine($"  状态: {req.State} | 目标: {req.TargetId ?? "广播"}");
            var lastResp = req.Responses.LastOrDefault();
            if (lastResp != null)
                sb.AppendLine($"  最后回应: [{lastResp.Type}] {lastResp.Content.Truncate(80)}");
        }

        return Task.FromResult(new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {},
        "required": []
    }
    """)!;
}
