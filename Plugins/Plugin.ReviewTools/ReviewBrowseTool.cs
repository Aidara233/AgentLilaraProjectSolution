using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewBrowseTool : ITool
{
    private readonly IReviewAccess _review;

    public ReviewBrowseTool(IToolContext ctx) => _review = ctx.Require<IReviewAccess>();

    public string Name => "review_browse";
    public string Description => "从游标处顺序读取当前频道消息，游标自动前进。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("count", "读取条数（默认20）", 0, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        int count = inputs.Count > 0 && int.TryParse(inputs[0], out var c) ? c : 20;

        if (_review.CursorChannelId == null)
            return new ToolResult { Status = "failed", Error = "游标未设置频道。请先用 review_focus 设置位置。" };

        var messages = await _review.BrowseAsync(count);
        if (messages.Count == 0)
            return new ToolResult { Status = "success", Data = "已到达频道末尾，没有更多消息。" };

        var lines = messages.Select(m => $"[{m.Time}] {m.SenderName}: {m.Content}");
        return new ToolResult { Status = "success", Data = string.Join("\n", lines) };
    }
}
