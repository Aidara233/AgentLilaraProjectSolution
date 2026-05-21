using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewGetPersonTool : ITool
{
    private readonly IReviewAccess _review;

    public ReviewGetPersonTool(IToolContext ctx) => _review = ctx.Require<IReviewAccess>();

    public string Name => "review_get_person";
    public string Description => "查询人物详情（信任等级/维度分数/称呼/快速记忆/关联账号）。当你在消息中注意到某个人物并想了解更多时使用。不要仅凭单条消息下结论。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("person_id", "人物ID", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0 || !int.TryParse(inputs[0], out var personId))
            return new ToolResult { Status = "failed", Error = "person_id 必须为整数" };

        var person = await _review.GetPersonAsync(personId);
        if (person == null)
            return new ToolResult { Status = "failed", Error = $"人物 P#{personId} 不存在" };

        _review.TrackPerson(personId);

        var lines = new List<string>
        {
            $"人物 P#{person.Id}",
            $"称呼: {(string.IsNullOrEmpty(person.Name) ? "（未设置）" : person.Name)}",
            $"别称: {(string.IsNullOrEmpty(person.Aliases) ? "（无）" : person.Aliases)}",
            $"信任等级: {person.TrustLevel}",
            $"警报等级: {person.AlertLevel}",
            $"快速记忆: {(string.IsNullOrEmpty(person.FastMemory) ? "（空）" : person.FastMemory)}"
        };

        if (person.Dimensions.Count > 0)
        {
            lines.Add("维度分数:");
            foreach (var d in person.Dimensions)
                lines.Add($"  {d.Dimension}: {d.Value:F1}");
        }
        else
        {
            lines.Add("维度分数: （尚无评价记录）");
        }

        return new ToolResult { Status = "success", Data = string.Join("\n", lines) };
    }
}
