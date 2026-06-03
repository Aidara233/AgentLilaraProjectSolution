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
        new("message_id", "基准消息的数据库ID（从 search 结果获取）", 0, false),
        new("offset", "相对基准的条数偏移（负值=往前N条，如-30）", 1, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var review = _ctx.Require<IReviewAccess>();
        int? messageId = inputs.Count > 0 && int.TryParse(inputs[0], out var mid) ? mid : null;
        int offset = inputs.Count > 1 && int.TryParse(inputs[1], out var off) ? off : 0;

        int baseMessageId;
        int channelId;

        if (messageId != null)
        {
            // 以指定消息为基准
            var msg = await review.GetMessageByIdAsync(messageId.Value);
            if (msg == null)
                return new ToolResult { Status = "failed", Error = $"消息 #{messageId} 不存在" };
            if (msg.ChannelId == null)
                return new ToolResult { Status = "failed", Error = $"消息 #{messageId} 无频道信息" };

            baseMessageId = msg.Id;
            channelId = msg.ChannelId.Value;
        }
        else
        {
            // 以当前游标为基准
            if (review.CursorChannelId == null || review.CursorMessageId == null)
                return new ToolResult { Status = "failed", Error = "未提供 message_id 且当前游标未设置。请先用 search 或 browse 定位后使用 offset。" };

            baseMessageId = review.CursorMessageId.Value;
            channelId = review.CursorChannelId.Value;
        }

        // 计算目标位置
        int targetMessageId;
        if (offset == 0)
        {
            targetMessageId = baseMessageId;
        }
        else
        {
            var target = await review.GetMessageOffsetAsync(channelId, baseMessageId, offset);
            if (!target.HasValue)
                return new ToolResult { Status = "failed", Error = "偏移计算失败，目标位置超出范围。" };
            targetMessageId = target.Value;
        }

        review.MoveCursor(targetMessageId, channelId);
        review.TrackChannel(channelId);

        var pos = $"频道#{channelId} 消息#{targetMessageId}";
        if (offset != 0)
            pos += $"（从消息#{baseMessageId} 偏移 {offset:+0;-#} 条）";
        return new ToolResult { Status = "success", Data = $"游标已移动到: {pos}" };
    }
}
