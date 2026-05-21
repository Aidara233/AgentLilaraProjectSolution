using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewListBeaconsTool : ITool
{
    private readonly IReviewAccess _review;

    public ReviewListBeaconsTool(IToolContext ctx) => _review = ctx.Require<IReviewAccess>();

    public string Name => "review_list_beacons";
    public string Description => "获取未处理信标列表。处理完初始种子后可主动拉取更多。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var beacons = await _review.GetUnprocessedBeaconsAsync();
        if (beacons.Count == 0)
            return new ToolResult { Status = "success", Data = "没有未处理的信标。" };

        var lines = beacons.Select(b =>
        {
            var location = b.ChannelId != null ? $"频道#{b.ChannelId}" : "";
            if (b.MessageId != null) location += $" 消息#{b.MessageId}";
            if (b.PersonId != null) location += $" 人物P#{b.PersonId}";
            var source = b.Source == "framework" ? " [自动]" : "";
            return $"- [{b.CreatedAt}]{source} {location}: {b.Content}";
        });

        return new ToolResult { Status = "success", Data = string.Join("\n", lines) };
    }
}
