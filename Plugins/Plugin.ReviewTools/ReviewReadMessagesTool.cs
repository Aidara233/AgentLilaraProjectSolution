using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewReadMessagesTool : ITool
{
    private readonly IChannelAccess _channels;

    public ReviewReadMessagesTool(IToolContext ctx)
    {
        _channels = ctx.Require<IChannelAccess>();
    }

    public string Name => "review_read_messages";
    public string Description => "读取指定频道的消息历史。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("channel_id", "频道ID", 0),
        new("count", "读取条数（默认20，最大50）", 1, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0 || !int.TryParse(inputs[0], out var channelId))
            return new ToolResult { Status = "failed", Error = "channel_id 必须是有效整数" };

        int count = inputs.Count > 1 && int.TryParse(inputs[1], out var c) ? Math.Min(c, 50) : 20;

        var messages = await _channels.GetMessagesAsync(channelId, count);
        if (messages.Count == 0)
            return new ToolResult { Status = "success", Data = "该频道无消息记录。" };

        var lines = messages.Select(m =>
            $"[{m.Timestamp:MM-dd HH:mm}] {m.UserName}: {m.Content}");
        return new ToolResult { Status = "success", Data = string.Join("\n", lines) };
    }
}
