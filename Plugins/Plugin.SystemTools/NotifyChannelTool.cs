// Plugins/Plugin.SystemTools/NotifyChannelTool.cs
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "向频道循环注入通知")]
public class NotifyChannelTool : ITool
{
    private readonly IChannelAccess _channels;

    public NotifyChannelTool(IChannelAccess channels)
    {
        _channels = channels;
    }

    public string Name => "notify_channel";
    public string Description => "向指定频道循环注入通知内容，由频道循环自行决定如何回应。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("channel_id", "目标频道ID", 0),
        new("content", "通知内容", 1)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var channelIdStr = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var content = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;

        if (string.IsNullOrWhiteSpace(channelIdStr) || !int.TryParse(channelIdStr, out var channelId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "channel_id 必须是有效的整数" });
        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "content 不能为空" });

        _channels.NotifyChannel(channelId, content);

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"已向频道 {channelId} 注入通知。"
        });
    }

    public JsonNode GetInputSchema()
    {
        return JsonNode.Parse("""
        {
            "type": "object",
            "properties": {
                "channel_id": { "type": "string", "description": "目标频道ID" },
                "content": { "type": "string", "description": "通知内容" }
            },
            "required": ["channel_id", "content"]
        }
        """)!;
    }
}
