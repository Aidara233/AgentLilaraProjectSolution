using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "汇报请求执行进度")]
public class ReportProgressTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public ReportProgressTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "report_progress";
    public string Description => "向请求发起者汇报当前执行进度。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("request_id", "请求ID", 0),
        new("progress", "进度描述", 1)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var requestId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var progress = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;

        if (string.IsNullOrWhiteSpace(requestId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "request_id 不能为空" });
        if (string.IsNullOrWhiteSpace(progress))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "progress 不能为空" });

        var ok = _messaging.Respond(requestId, CrossRequestResponseType.Progress, progress);
        if (!ok)
            return Task.FromResult(new ToolResult
            {
                Status = "failed",
                Error = $"请求 {requestId} 不存在或状态不可操作"
            });

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = "进度已汇报。"
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "request_id": { "type": "string", "description": "请求ID" },
            "progress": { "type": "string", "description": "进度描述" }
        },
        "required": ["request_id", "progress"]
    }
    """)!;
}
