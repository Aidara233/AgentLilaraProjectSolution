using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "评估收到的委托请求（系统循环用）")]
public class EvaluateRequestTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public EvaluateRequestTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "evaluate_request";
    public string Description => "评估收到的跨循环请求。accept=接受并开始执行；reject=拒绝并附理由；queue 暂不支持（按 reject 处理）。接受后应通过 report_progress 汇报进度，完成后调用 complete_request。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("request_id", "请求ID", 0),
        new("verdict", "评估结果: accept / reject / queue（暂作reject处理）", 1),
        new("reason", "评估理由", 2)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var requestId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var verdict = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;
        var reason = resolvedInputs.Count > 2 ? resolvedInputs[2] : null;

        if (string.IsNullOrWhiteSpace(requestId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "request_id 不能为空" });
        if (string.IsNullOrWhiteSpace(verdict))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "verdict 不能为空" });

        CrossRequestResponseType type = verdict.ToLower() switch
        {
            "accept" => CrossRequestResponseType.Accept,
            "reject" => CrossRequestResponseType.Reject,
            "queue" => CrossRequestResponseType.Reject, // 当前不支持排队，转为拒绝
            _ => CrossRequestResponseType.Reject
        };

        var ok = _messaging.Respond(requestId, type, reason ?? "");
        if (!ok)
            return Task.FromResult(new ToolResult
            {
                Status = "failed",
                Error = $"请求 {requestId} 不存在或状态不可操作（可能已超时/归档）"
            });

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"请求 {requestId} 已评估为 {verdict}。"
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "request_id": { "type": "string", "description": "请求ID" },
            "verdict": { "type": "string", "enum": ["accept", "reject", "queue"], "description": "评估结果" },
            "reason": { "type": "string", "description": "评估理由" }
        },
        "required": ["request_id", "verdict", "reason"]
    }
    """)!;
}
