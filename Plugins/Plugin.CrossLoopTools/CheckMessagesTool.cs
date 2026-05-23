using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "查看当前循环收到的委托请求")]
public class CheckMessagesTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public CheckMessagesTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "check_messages";
    public string Description => "查看当前循环收到的待处理跨循环请求。";

    public IReadOnlyList<ToolParameter> Parameters => [];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var requests = _messaging.Receive(10);
        if (requests.Count == 0)
            return Task.FromResult(new ToolResult { Status = "success", Data = "没有待处理的请求。" });

        var lines = new List<string>();
        foreach (var r in requests)
        {
            lines.Add($"#{r.Id[..8]}: [{r.State}] {r.Title} (来自: {r.InitiatorId})");
            if (r.Responses.Count > 0)
            {
                foreach (var resp in r.Responses)
                    lines.Add($"  {resp.Type}: {resp.Content.Truncate(80)}");
            }
        }

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = string.Join("\n", lines)
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
