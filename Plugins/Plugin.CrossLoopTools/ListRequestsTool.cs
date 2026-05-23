using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "列出当前循环可见的请求")]
public class ListRequestsTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public ListRequestsTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "list_requests";
    public string Description => "列出当前循环可见的活跃和已完成跨循环请求。";

    public IReadOnlyList<ToolParameter> Parameters => [];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var active = _messaging.GetActiveRequests();
        var completed = _messaging.GetCompletedRequests();

        var lines = new List<string>();

        if (active.Count > 0)
        {
            lines.Add($"── 活跃请求 ({active.Count}) ──");
            foreach (var r in active)
                lines.Add($"  #{r.Id[..8]}: [{r.State}] {r.Title} | 目标:{r.TargetId ?? "广播"}");
        }

        if (completed.Count > 0)
        {
            lines.Add($"── 已完成 ({completed.Count}) ──");
            foreach (var r in completed)
            {
                var lastResp = r.Responses.LastOrDefault();
                lines.Add($"  #{r.Id[..8]}: [{r.State}] {r.Title}"
                    + (lastResp != null ? $" | 结果:{lastResp.Content.Truncate(60)}" : ""));
            }
        }

        if (active.Count == 0 && completed.Count == 0)
            return Task.FromResult(new ToolResult { Status = "success", Data = "没有可见的请求。" });

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
