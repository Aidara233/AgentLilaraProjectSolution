using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewFocusTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewFocusTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_focus";
    public string Description => "移动阅读游标。使用 offset 时建议偏大（如 -30 ~ -50），多读几条无关消息的代价远小于错过关键上下文。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("message_id", "数据库消息ID（消息中的 db_id，非平台 id）", 0, false),
        new("offset", "相对 message_id 偏移（负值=往前）", 1, false),
        new("channel_id", "内部频道ID（频道信息中的 频道ID）", 2, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var review = _ctx.Require<IReviewAccess>();
        int? messageId = inputs.Count > 0 && int.TryParse(inputs[0], out var mid) ? mid : null;
        int offset = inputs.Count > 1 && int.TryParse(inputs[1], out var off) ? off : 0;
        int? channelId = inputs.Count > 2 && int.TryParse(inputs[2], out var cid) ? cid : null;

        if (messageId == null && channelId == null)
            return new ToolResult { Status = "failed", Error = "需要提供 message_id 或 channel_id" };

        if (messageId != null)
        {
            // 若未提供 channel_id 且游标无频道，按 message_id 推导
            if (channelId == null && review.CursorChannelId == null)
            {
                var msg = await review.GetMessageByIdAsync(messageId.Value);
                if (msg == null)
                    return new ToolResult { Status = "failed", Error = $"消息 #{messageId} 不存在，无法推导频道" };
                channelId = msg.ChannelId;
            }

            review.MoveCursor(messageId.Value + offset, channelId ?? review.CursorChannelId);
            if (channelId != null) review.TrackChannel(channelId.Value);
        }
        else
        {
            review.MoveCursor(null, channelId);
            review.TrackChannel(channelId!.Value);
        }

        var pos = review.CursorMessageId != null
            ? $"频道#{review.CursorChannelId} 消息#{review.CursorMessageId}"
            : $"频道#{review.CursorChannelId} 最新位置";
        return new ToolResult { Status = "success", Data = $"游标已移动到: {pos}" };
    }
}
