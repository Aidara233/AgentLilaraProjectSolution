using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class GetPersonTraitsTool : ITool
{
    private readonly IToolContext _ctx;

    public GetPersonTraitsTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "get_person_traits";
    public string Description => "查询某人的结构化特质列表（偏好/习惯/风格/关系/专长等）。按置信度降序排列。可选按分类筛选。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("person_id", "内部人物ID", 0),
        new("category", "特质分类筛选（preference/habit/style/relationship/expertise），留空返回全部", 1, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var review = _ctx.Require<IReviewAccess>();
        if (inputs.Count == 0 || !int.TryParse(inputs[0], out var personId))
            return new ToolResult { Status = "failed", Error = "person_id 必须为整数" };

        var category = inputs.Count > 1 && !string.IsNullOrWhiteSpace(inputs[1]) ? inputs[1].Trim() : null;

        var traits = await review.GetPersonTraitsAsync(personId, category);
        review.TrackPerson(personId);

        if (traits.Count == 0)
        {
            var catMsg = category != null ? $"（分类: {category}）" : "";
            return new ToolResult
            {
                Status = "success",
                Data = $"P#{personId} 暂无结构化特质记录{catMsg}。使用 update_person_trait 创建。"
            };
        }

        var lines = new List<string> { $"P#{personId} 结构化特质 ({traits.Count} 条):" };
        var lastCat = "";
        foreach (var t in traits)
        {
            if (t.Category != lastCat)
            {
                lines.Add($"\n[{t.Category}]");
                lastCat = t.Category;
            }
            lines.Add($"  {t.Key}: {t.Value} (置信度 {t.Confidence:F1})");
        }

        return new ToolResult { Status = "success", Data = string.Join("\n", lines) };
    }
}
