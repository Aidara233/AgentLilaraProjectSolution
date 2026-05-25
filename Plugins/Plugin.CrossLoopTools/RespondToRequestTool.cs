using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "回应一个跨循环请求")]
public class RespondToRequestTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public RespondToRequestTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "respond_to_request";
    public string Description => "对收到的跨循环请求做出回应。accept=接受委托；reject=拒绝；progress=汇报进度；complete=标记成功；failed=标记失败；ignore=忽略后从视野消失。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("request_id", "请求ID", 0),
        new("type", "回应类型: accept / reject / progress / complete / failed / ignore", 1),
        new("content", "回应内容", 2)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    private static readonly Dictionary<string, CrossRequestResponseType> s_typeMap = new()
    {
        ["accept"] = CrossRequestResponseType.Accept,
        ["reject"] = CrossRequestResponseType.Reject,
        ["progress"] = CrossRequestResponseType.Progress,
        ["complete"] = CrossRequestResponseType.Complete,
        ["failed"] = CrossRequestResponseType.Failed,
        ["ignore"] = CrossRequestResponseType.Ignore,
    };

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var requestId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var typeStr = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;
        var content = resolvedInputs.Count > 2 ? resolvedInputs[2] : null;

        if (string.IsNullOrWhiteSpace(requestId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "request_id 不能为空" });

        if (string.IsNullOrWhiteSpace(typeStr) || !s_typeMap.TryGetValue(typeStr.ToLower(), out var type))
            return Task.FromResult(new ToolResult { Status = "failed",
                Error = "type 必须是 accept/reject/progress/complete/failed/ignore" });

        var ok = _messaging.Respond(requestId, type, content ?? "");
        if (!ok)
            return Task.FromResult(new ToolResult
            {
                Status = "failed",
                Error = $"请求 {requestId} 不存在或状态不可操作"
            });

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"已回应请求 {requestId}：{typeStr}。"
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "request_id": { "type": "string", "description": "请求ID" },
            "type": { "type": "string", "enum": ["accept", "reject", "progress", "complete", "failed", "ignore"], "description": "回应类型" },
            "content": { "type": "string", "description": "回应内容" }
        },
        "required": ["request_id", "type", "content"]
    }
    """)!;
}
