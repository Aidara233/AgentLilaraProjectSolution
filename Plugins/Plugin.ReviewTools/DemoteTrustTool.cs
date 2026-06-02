using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class DemoteTrustTool : ITool
{
    private readonly IToolContext _ctx;

    public DemoteTrustTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "demote_trust";
    public string Description => "降低某人的信任等级。仅当 Review 探索后确认此人不再满足当前等级要求时使用。降级需提供明确理由。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("person_id", "内部人物ID", 0),
        new("reason", "降级原因（必填，说明为什么不再符合当前等级标准）", 1)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var review = _ctx.Require<IReviewAccess>();
        if (inputs.Count < 2 || !int.TryParse(inputs[0], out var personId))
            return new ToolResult { Status = "failed", Error = "person_id 和 reason 为必填" };

        var reason = inputs[1].Trim();
        if (string.IsNullOrWhiteSpace(reason))
            return new ToolResult { Status = "failed", Error = "降级原因不能为空" };

        review.TrackPerson(personId);

        var success = await review.DemoteTrustAsync(personId, reason);
        if (!success)
            return new ToolResult
            {
                Status = "failed",
                Error = "降级失败。此人可能已处于最低可降级等级（Unknown 及以下不可降级）"
            };

        return new ToolResult
        {
            Status = "success",
            Data = $"已降级 P#{personId}，原因: {reason}。降级后信任等级已降低一级。"
        };
    }
}
