using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewUpdateAffinityTool : ITool
{
    private readonly IChannelAccess _channels;

    public ReviewUpdateAffinityTool(IToolContext ctx)
    {
        _channels = ctx.Require<IChannelAccess>();
    }

    public string Name => "review_update_affinity";
    public string Description => "调整频道亲和度。正值增加参与倾向，负值降低。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("channel_id", "频道ID", 0),
        new("delta", "调整量（如 +0.1 或 -0.2）", 1)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (inputs.Count < 2 || !int.TryParse(inputs[0], out var channelId))
            return new ToolResult { Status = "failed", Error = "channel_id 必须是有效整数" };

        if (!float.TryParse(inputs[1], out var delta))
            return new ToolResult { Status = "failed", Error = "delta 必须是有效数字" };

        delta = Math.Clamp(delta, -0.5f, 0.5f);
        await _channels.UpdateAffinityAsync(channelId, delta);

        return new ToolResult { Status = "success", Data = $"频道 {channelId} 亲和度已调整 {delta:+0.0;-0.0}" };
    }
}
