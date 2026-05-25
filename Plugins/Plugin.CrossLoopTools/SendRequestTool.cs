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
    public string Description => "向指定目标循环（或广播）发起委托请求。提交后立即返回，不阻塞。\n状态变更（接受/拒绝/完成等）将通过通知自动送达。\ntarget_id 留空 = 广播；填 system = 向系统循环发起；填 channel:N = 向指定频道发起。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("title", "请求标题", 0),
        new("content", "请求详细内容", 1),
        new("target_id", "目标循环ID（留空广播）", 2, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var title = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var content = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;
        var targetId = resolvedInputs.Count > 2 && !string.IsNullOrWhiteSpace(resolvedInputs[2])
            ? resolvedInputs[2] : null;

        if (string.IsNullOrWhiteSpace(title))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "title 不能为空" });
        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "content 不能为空" });

        var requestId = _messaging.SubmitFireAndForget(targetId, title, content);

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"委托已提交。request_id: {requestId}。状态变更将通过通知送达。"
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "title": { "type": "string", "description": "请求标题" },
            "content": { "type": "string", "description": "请求详细内容" },
            "target_id": { "type": "string", "description": "目标循环ID（留空则广播给所有活跃循环）" }
        },
        "required": ["title", "content"]
    }
    """)!;
}
