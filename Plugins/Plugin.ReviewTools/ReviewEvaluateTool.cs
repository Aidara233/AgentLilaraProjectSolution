using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewEvaluateTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewEvaluateTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_evaluate";
    public string Description => "随时记录你对人物/频道的印象。可以多次评价同一目标同维度，最终取平均值应用。不用纠结，跟着感觉走。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("target_type", "目标类型: person 或 channel", 0),
        new("target_id", "内部ID（person_id 或 channel_id，见上下文）", 1),
        new("dimension", "维度: reliability/respect/value/stability（人物）或 value（频道）", 2),
        new("rating", "评价: ++/+/0/-/--", 3)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> ValidRatings = new() { "++", "+", "0", "-", "--" };
    private static readonly HashSet<string> PersonDimensions = new() { "reliability", "respect", "value", "stability" };
    private static readonly HashSet<string> ChannelDimensions = new() { "value" };

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var review = _ctx.Require<IReviewAccess>();
        if (inputs.Count < 4)
            return Task.FromResult(new ToolResult { Status = "failed", Error = "需要 target_type, target_id, dimension, rating 四个参数" });

        var targetType = inputs[0];
        if (targetType != "person" && targetType != "channel")
            return Task.FromResult(new ToolResult { Status = "failed", Error = "target_type 必须为 person 或 channel" });

        if (!int.TryParse(inputs[1], out var targetId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "target_id 必须为整数" });

        var dimension = inputs[2];
        var validDims = targetType == "person" ? PersonDimensions : ChannelDimensions;
        if (!validDims.Contains(dimension))
            return Task.FromResult(new ToolResult { Status = "failed", Error = $"无效维度。{targetType} 可用: {string.Join("/", validDims)}" });

        var rating = inputs[3];
        if (!ValidRatings.Contains(rating))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "rating 必须为 ++/+/0/-/--" });

        review.AddEvaluation(targetType, targetId, dimension, rating);

        if (targetType == "person") review.TrackPerson(targetId);
        else review.TrackChannel(targetId);

        return Task.FromResult(new ToolResult { Status = "success", Data = $"已记录: {targetType}#{targetId} {dimension} {rating}" });
    }
}
