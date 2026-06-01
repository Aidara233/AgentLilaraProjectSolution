using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewSearchMessagesTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewSearchMessagesTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_search_messages";
    public string Description => "按条件搜索消息，结果带消息ID（可用于 review_focus 跳转）。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("query", "搜索关键词", 0),
        new("channel_id", "限定频道ID", 1, false),
        new("person_id", "限定人物ID", 2, false),
        new("time_start", "起始时间（yyyy-MM-dd）", 3, false),
        new("time_end", "结束时间（yyyy-MM-dd）", 4, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var review = _ctx.Require<IReviewAccess>();
        var query = inputs.Count > 0 ? inputs[0] : null;
        int? channelId = inputs.Count > 1 && int.TryParse(inputs[1], out var cid) ? cid : null;
        int? personId = inputs.Count > 2 && int.TryParse(inputs[2], out var pid) ? pid : null;
        var timeStart = inputs.Count > 3 ? inputs[3] : null;
        var timeEnd = inputs.Count > 4 ? inputs[4] : null;

        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult { Status = "failed", Error = "query 不能为空" };

        var messages = await review.SearchMessagesAsync(query, channelId, personId, timeStart, timeEnd, 30);
        if (messages.Count == 0)
            return new ToolResult { Status = "success", Data = "未找到匹配消息。" };

        var lines = messages.Select(m => $"[ID:{m.PlatformMessageId ?? m.Id.ToString()}] [{m.Time}] {m.SenderName}: {m.Content}");
        return new ToolResult { Status = "success", Data = string.Join("\n", lines) };
    }
}
