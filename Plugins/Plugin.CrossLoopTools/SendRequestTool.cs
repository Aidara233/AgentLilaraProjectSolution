using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "向其他循环发起委托请求")]
public class SendRequestTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public SendRequestTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "send_request";
    public string Description => "向指定目标循环（或广播）发起委托请求，等待评估结果。target_id 留空则广播给所有活跃循环。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("title", "请求标题", 0),
        new("content", "请求详细内容", 1),
        new("target_id", "目标循环ID（留空广播）", 2, isRequired: false),
        new("timeout_seconds", "等待超时秒数（默认45）", 3, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(50);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var title = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var content = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;
        var targetId = resolvedInputs.Count > 2 && !string.IsNullOrWhiteSpace(resolvedInputs[2])
            ? resolvedInputs[2] : null;
        var timeoutSec = resolvedInputs.Count > 3 && int.TryParse(resolvedInputs[3], out var s) ? s : 45;

        if (string.IsNullOrWhiteSpace(title))
            return new ToolResult { Status = "failed", Error = "title 不能为空" };
        if (string.IsNullOrWhiteSpace(content))
            return new ToolResult { Status = "failed", Error = "content 不能为空" };

        var result = await _messaging.SubmitAndWaitAsync(
            targetId, title, content,
            timeout: TimeSpan.FromSeconds(timeoutSec));

        if (result.TimedOut)
            return new ToolResult
            {
                Status = "success",
                Data = $"请求已发出但等待回应超时。request_id: {result.RequestId}"
            };

        return new ToolResult
        {
            Status = "success",
            Data = $"请求已处理。request_id: {result.RequestId}, 结果: {result.Verdict}"
                + (result.Result != null ? $"\n详情: {result.Result}" : "")
        };
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "title": { "type": "string", "description": "请求标题" },
            "content": { "type": "string", "description": "请求详细内容" },
            "target_id": { "type": "string", "description": "目标循环ID（留空则广播给所有活跃循环）" },
            "timeout_seconds": { "type": "integer", "description": "等待超时秒数（默认45）" }
        },
        "required": ["title", "content"]
    }
    """)!;
}
