using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true)]
public class SendMessageTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public SendMessageTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "send_message";
    public string Description =>
        "向指定目标循环发送一条单发消息（fire-and-forget，无回应状态机）。\n" +
        "target_id 留空 = 广播给所有活跃循环；填 channel:N = 向指定频道循环发起；填 session:xxx = 向指定 session 发起；填 task:xxx = 向指定任务发起；填 review:xxx = 向复盘会话发起。\n" +
        "对方循环在下一轮上下文中将收到 [循环消息] 注入。回复就是对方再发一条新消息（target_id 填你的 loopId）。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("content", "消息内容", 0),
        new("target_id", "目标循环ID（留空广播）", 1, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var content = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var targetId = resolvedInputs.Count > 1 && !string.IsNullOrWhiteSpace(resolvedInputs[1])
            ? resolvedInputs[1] : null;

        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "content 不能为空" });

        var messageId = _messaging.SendMessage(targetId, content);

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"消息已发送。message_id: {messageId[..8]}。目标循环下一轮可看到。"
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "content": { "type": "string", "description": "消息内容" },
            "target_id": { "type": "string", "description": "目标循环ID（留空则广播给所有活跃循环）" }
        },
        "required": ["content"]
    }
    """)!;
}