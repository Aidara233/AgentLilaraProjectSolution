using System.Text;
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "查询单个跨循环请求的详情")]
public class GetRequestTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public GetRequestTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "get_request";
    public string Description => "按ID查询单个跨循环请求的详情，包括当前状态、所有回应记录。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("request_id", "请求ID", 0)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var requestId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;

        if (string.IsNullOrWhiteSpace(requestId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "request_id 不能为空" });

        var req = _messaging.Get(requestId);
        if (req == null)
            return Task.FromResult(new ToolResult { Status = "failed", Error = $"请求 {requestId} 不存在" });

        var sb = new StringBuilder();
        sb.AppendLine($"请求 #{req.Id[..8]}: {req.Title}");
        sb.AppendLine($"发起者: {req.InitiatorId}");
        sb.AppendLine($"目标: {req.TargetId ?? "广播"}");
        sb.AppendLine($"状态: {req.State}");
        sb.AppendLine($"内容: {req.Content}");
        if (req.Responses.Count > 0)
        {
            sb.AppendLine("回应记录:");
            foreach (var resp in req.Responses)
                sb.AppendLine($"  [{resp.Type}] {resp.ResponderId}: {resp.Content.Truncate(120)}");
        }

        return Task.FromResult(new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "request_id": { "type": "string", "description": "请求ID" }
        },
        "required": ["request_id"]
    }
    """)!;
}
