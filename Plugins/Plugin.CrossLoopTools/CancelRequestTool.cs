using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "取消（归档）一个自己发起的请求")]
public class CancelRequestTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public CancelRequestTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "cancel_request";
    public string Description => "归档一个自己发起的请求。如果请求正在执行中，接受者会收到委托被归档的通知。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("request_id", "要取消的请求ID", 0)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var requestId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        if (string.IsNullOrWhiteSpace(requestId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "request_id 不能为空" });

        var req = _messaging.Get(requestId);
        if (req == null)
            return Task.FromResult(new ToolResult { Status = "failed", Error = $"请求 {requestId} 不存在" });

        _messaging.Archive(requestId);
        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"请求 {requestId} 已归档。接受者会收到归档通知。"
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "request_id": { "type": "string", "description": "要取消的请求ID" }
        },
        "required": ["request_id"]
    }
    """)!;
}
