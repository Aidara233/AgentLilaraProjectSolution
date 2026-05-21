using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewUpdatePersonTool : ITool
{
    private readonly IPersonAccess _persons;

    public ReviewUpdatePersonTool(IToolContext ctx)
    {
        _persons = ctx.Require<IPersonAccess>();
    }

    public string Name => "review_update_person";
    public string Description => "更新人物信息：称呼、别称、快速记忆。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("person_id", "人物ID", 0),
        new("field", "要更新的字段：name / aliases / fast_memory", 1),
        new("value", "新值", 2)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (inputs.Count < 3 || !int.TryParse(inputs[0], out var personId))
            return new ToolResult { Status = "failed", Error = "需要 person_id, field, value 三个参数" };

        var field = inputs[1]?.Trim().ToLower();
        var value = inputs[2];

        switch (field)
        {
            case "name":
                await _persons.UpdateNameAsync(personId, value);
                break;
            case "aliases":
                await _persons.UpdateNameAsync(personId, value, value);
                break;
            case "fast_memory":
                await _persons.UpdateFastMemoryAsync(personId, value);
                break;
            default:
                return new ToolResult { Status = "failed", Error = $"未知字段: {field}，支持 name/aliases/fast_memory" };
        }

        return new ToolResult { Status = "success", Data = $"已更新人物 {personId} 的 {field}" };
    }
}
