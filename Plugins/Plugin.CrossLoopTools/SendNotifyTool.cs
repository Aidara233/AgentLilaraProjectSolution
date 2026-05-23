using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "发送一次性通知给其他循环")]
public class SendNotifyTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public SendNotifyTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "send_notify";
    public string Description => "向指定目标循环发送一次性通知（fire-and-forget，不等待回应）。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("title", "通知标题", 0),
        new("content", "通知内容", 1),
        new("target_id", "目标循环ID", 2, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

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

        var id = _messaging.SubmitFireAndForget(targetId, title, content);

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"通知已发送。request_id: {id}"
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "title": { "type": "string", "description": "通知标题" },
            "content": { "type": "string", "description": "通知内容" },
            "target_id": { "type": "string", "description": "目标循环ID" }
        },
        "required": ["title", "content"]
    }
    """)!;
}
