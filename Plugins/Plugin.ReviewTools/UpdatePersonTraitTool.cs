using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class UpdatePersonTraitTool : ITool
{
    private readonly IToolContext _ctx;

    public UpdatePersonTraitTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "update_person_trait";
    public string Description => "添加或更新某人的结构化特质。用于记录已确认的偏好、习惯、专长等。同一 category+key 会自动覆盖旧值。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("person_id", "内部人物ID", 0),
        new("category", "特质分类: preference/habit/style/relationship/expertise", 1),
        new("key", "属性名（如'食物偏好'、'作息'、'沟通风格'）", 2),
        new("value", "属性值", 3),
        new("confidence", "置信度 0-1.0（默认 0.7）", 4, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var review = _ctx.Require<IReviewAccess>();
        if (inputs.Count < 4 || !int.TryParse(inputs[0], out var personId))
            return new ToolResult { Status = "failed", Error = "person_id / category / key / value 为必填" };

        var category = inputs[1].Trim();
        var key = inputs[2].Trim();
        var value = inputs[3].Trim();
        var confidence = 0.7f;
        if (inputs.Count > 4 && float.TryParse(inputs[4], out var c))
            confidence = Math.Clamp(c, 0f, 1f);

        review.TrackPerson(personId);
        await review.UpsertPersonTraitAsync(personId, category, key, value, confidence);
        await review.LogActionAsync("update_person_trait",
            $"P#{personId} [{category}] {key} = {value} (置信度 {confidence:F1})");

        return new ToolResult
        {
            Status = "success",
            Data = $"已更新 P#{personId} 特质 [{category}] {key}: {value} (置信度 {confidence:F1})"
        };
    }
}
