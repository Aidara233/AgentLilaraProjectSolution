using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewFocusTool : ITool
{
    private readonly IReviewAccess _review;

    public ReviewFocusTool(IToolContext ctx) => _review = ctx.Require<IReviewAccess>();

    public string Name => "review_focus";
    public string Description => "移动阅读游标。使用 offset 时建议偏大（如 -30 ~ -50），多读几条无关消息的代价远小于错过关键上下文。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("message_id", "跳到指定消息ID", 0, false),
        new("offset", "相对 message_id 偏移（负值=往前）", 1, false),
        new("channel_id", "跳到该频道最新位置（无 message_id 时使用）", 2, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        int? messageId = inputs.Count > 0 && int.TryParse(inputs[0], out var mid) ? mid : null;
        int offset = inputs.Count > 1 && int.TryParse(inputs[1], out var off) ? off : 0;
        int? channelId = inputs.Count > 2 && int.TryParse(inputs[2], out var cid) ? cid : null;

        if (messageId == null && channelId == null)
            return Task.FromResult(new ToolResult { Status = "failed", Error = "需要提供 message_id 或 channel_id" });

        if (messageId != null)
        {
            _review.MoveCursor(messageId.Value + offset, channelId ?? _review.CursorChannelId);
            if (channelId != null) _review.TrackChannel(channelId.Value);
        }
        else
        {
            _review.MoveCursor(null, channelId);
            _review.TrackChannel(channelId!.Value);
        }

        var pos = _review.CursorMessageId != null
            ? $"频道#{_review.CursorChannelId} 消息#{_review.CursorMessageId}"
            : $"频道#{_review.CursorChannelId} 最新位置";
        return Task.FromResult(new ToolResult { Status = "success", Data = $"游标已移动到: {pos}" });
    }
}
