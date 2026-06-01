using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewUpdatePersonTool : ITool
{
    private readonly IPersonAccess _person;
    private readonly IToolContext _ctx;

    public ReviewUpdatePersonTool(IToolContext ctx)
    {
        _person = ctx.Require<IPersonAccess>();
        _ctx = ctx;
    }

    public string Name => "review_update_person";
    public string Description => "更新人物基础信息（称呼/别称/快速记忆）。更新前请先 review_get_person 了解当前状态，确认有实质变化再修改。评价请用 review_evaluate。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("person_id", "人物ID", 0),
        new("name", "新称呼（留空不改）", 1, false),
        new("aliases", "别称（逗号分隔，留空不改）", 2, false),
        new("fast_memory", "快速记忆（一句话概括，留空不改）", 3, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var review = _ctx.Require<IReviewAccess>();
        if (inputs.Count == 0 || !int.TryParse(inputs[0], out var personId))
            return new ToolResult { Status = "failed", Error = "person_id 必须为整数" };

        var name = inputs.Count > 1 && !string.IsNullOrWhiteSpace(inputs[1]) ? inputs[1] : null;
        var aliases = inputs.Count > 2 && !string.IsNullOrWhiteSpace(inputs[2]) ? inputs[2] : null;
        var fastMemory = inputs.Count > 3 && !string.IsNullOrWhiteSpace(inputs[3]) ? inputs[3] : null;

        if (name == null && aliases == null && fastMemory == null)
            return new ToolResult { Status = "failed", Error = "至少提供一个要修改的字段" };

        var changes = new List<string>();
        if (name != null || aliases != null)
        {
            await _person.UpdateNameAsync(personId, name ?? "", aliases);
            if (name != null) changes.Add($"称呼→{name}");
            if (aliases != null) changes.Add($"别称→{aliases}");
        }
        if (fastMemory != null) { await _person.UpdateFastMemoryAsync(personId, fastMemory); changes.Add($"快速记忆→{fastMemory}"); }

        var summary = $"更新P#{personId}: {string.Join(", ", changes)}";
        var detail = System.Text.Json.JsonSerializer.Serialize(new { personId, name, aliases, fastMemory });
        await review.LogActionAsync("update_person", summary, detail);

        review.TrackPerson(personId);
        return new ToolResult { Status = "success", Data = summary };
    }
}
