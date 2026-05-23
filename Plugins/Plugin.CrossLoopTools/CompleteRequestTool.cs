using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "标记请求完成并回传结果")]
public class CompleteRequestTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public CompleteRequestTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "complete_request";
    public string Description => "标记接受的请求已完成（或失败），结果回传给发起者。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("request_id", "请求ID", 0),
        new("result", "执行结果", 1),
        new("failed", "是否失败（true/false，默认false）", 2)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var requestId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var result = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;
        var failed = resolvedInputs.Count > 2 && resolvedInputs[2]?.ToLower() == "true";

        if (string.IsNullOrWhiteSpace(requestId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "request_id 不能为空" });
        if (string.IsNullOrWhiteSpace(result))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "result 不能为空" });

        _messaging.Respond(requestId,
            failed ? CrossRequestResponseType.Complete : CrossRequestResponseType.Complete,
            result);

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"请求 {requestId} 已标记为{(failed ? "失败" : "完成")}，结果已回传。"
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "request_id": { "type": "string", "description": "请求ID" },
            "result": { "type": "string", "description": "执行结果" },
            "failed": { "type": "string", "description": "是否失败（true/false，默认false）" }
        },
        "required": ["request_id", "result"]
    }
    """)!;
}
