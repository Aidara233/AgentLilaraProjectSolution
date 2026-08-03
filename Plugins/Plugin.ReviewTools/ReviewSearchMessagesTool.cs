using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewSearchMessagesTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewSearchMessagesTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_search_messages";
    public string Description => "按条件搜索消息，结果带消息ID（可用于 review_focus 跳转）。query、channel_id、person_id、time 至少提供一个。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("query", "搜索关键词（可选）", 0, false),
        new("channel_id", "内部频道ID（可选）", 1, false),
        new("person_id", "内部人物ID（可选）", 2, false),
        new("time_start", "起始时间（yyyy-MM-dd，可选）", 3, false),
        new("time_end", "结束时间（yyyy-MM-dd，可选）", 4, false),
        new("limit", "返回条数（1~100，默认20）", 5, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var review = _ctx.Require<IReviewAccess>();
        var query = inputs.Count > 0 && !string.IsNullOrWhiteSpace(inputs[0]) ? inputs[0] : null;
        int? channelId = inputs.Count > 1 && int.TryParse(inputs[1], out var cid) ? cid : null;
        int? personId = inputs.Count > 2 && int.TryParse(inputs[2], out var pid) ? pid : null;
        var timeStart = inputs.Count > 3 && !string.IsNullOrWhiteSpace(inputs[3]) ? inputs[3] : null;
        var timeEnd = inputs.Count > 4 && !string.IsNullOrWhiteSpace(inputs[4]) ? inputs[4] : null;
        int limit = inputs.Count > 5 && int.TryParse(inputs[5], out var lim) ? lim : 20;

        if (string.IsNullOrWhiteSpace(query) && channelId == null && personId == null
            && string.IsNullOrWhiteSpace(timeStart) && string.IsNullOrWhiteSpace(timeEnd))
            return new ToolResult { Status = "failed", Error = "至少提供一个搜索条件（query/channel_id/person_id/time_start/time_end）" };

        limit = Math.Clamp(limit, 1, 100);

        var messages = await review.SearchMessagesAsync(query, channelId, personId, timeStart, timeEnd, limit);
        if (messages.Count == 0)
            return new ToolResult { Status = "success", Data = "未找到匹配消息。" };

        var lines = messages.Select(m =>
        {
            var sender = m.PersonId != null ? $"{m.SenderName}(P#{m.PersonId})" : m.SenderName;
            return $"[ID:{m.Id}] [{m.Time}] {sender}: {m.Content}";
        });
        return new ToolResult { Status = "success", Data = string.Join("\n", lines) };
    }
}
