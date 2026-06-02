using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class PromoteTrustTool : ITool
{
    private readonly IToolContext _ctx;

    public PromoteTrustTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "promote_trust";
    public string Description => "提升某人的信任等级。仅允许逐级提升，且硬指标必须达标。调用前应先用 get_person_traits 和 review_get_person 充分了解此人。如果硬指标未达标，应通过 review_evaluate 提升相关维度分数。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("person_id", "内部人物ID", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var review = _ctx.Require<IReviewAccess>();
        if (inputs.Count == 0 || !int.TryParse(inputs[0], out var personId))
            return new ToolResult { Status = "failed", Error = "person_id 必须为整数" };

        review.TrackPerson(personId);

        // 先查硬指标状态
        var criteria = await review.GetTrustCriteriaAsync(personId);
        if (string.IsNullOrEmpty(criteria.NextLevel))
            return new ToolResult
            {
                Status = "failed",
                Error = $"此人当前信任等级为 {criteria.CurrentLevel}，已达到可自动升级的上限"
            };

        if (!criteria.HardCriteriaMet)
            return new ToolResult
            {
                Status = "failed",
                Data = $@"硬指标未达标，无法升级到 {criteria.NextLevelLabel}。

当前状态:
- 消息数: {criteria.MessageCount}
- 记忆数: {criteria.MemoryCount}
- 创建天数: {criteria.DaysSinceCreation}
- Review 次数: {criteria.ReviewCount}
- 维度分数: {(criteria.DimensionValues.Count > 0 ? string.Join(", ", criteria.DimensionValues.Select(kv => $"{kv.Key}={kv.Value:F1}")) : "（无）")}

{criteria.HardCriteriaDetail}。如果认为此人确实值得升级但硬指标未达标，可通过 review_evaluate 提升相关维度分数。"
            };

        var success = await review.PromoteTrustAsync(personId);
        if (!success)
            return new ToolResult { Status = "failed", Error = "升级失败，请确认硬指标已达标" };

        await review.LogActionAsync("promote_trust",
            $"P#{personId} {criteria.CurrentLevel} → {criteria.NextLevelLabel}");

        return new ToolResult
        {
            Status = "success",
            Data = $"已升级 P#{personId}: {criteria.CurrentLevel} → {criteria.NextLevelLabel}。建议同时更新此人的人物特质 (update_person_trait)，记录升级依据。"
        };
    }
}
