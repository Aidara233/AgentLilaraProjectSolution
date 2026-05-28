using AgentLilara.PluginSDK;

namespace Plugin.SkillTools;

[ToolMeta(ContinueLoop = false, CapabilitySummary = "技能：列出所有可用技能")]
public class ListSkillsTool : ITool
{
    private readonly Func<IReadOnlyList<SkillEntry>> _getSkills;

    public ListSkillsTool(Func<IReadOnlyList<SkillEntry>> getSkills)
    {
        _getSkills = getSkills;
    }

    public string Name => "list_skills";
    public string Description => "列出当前所有可用技能及其描述。";
    public IReadOnlyList<ToolParameter> Parameters => [];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var skills = _getSkills();
        if (skills.Count == 0)
            return Task.FromResult(Ok("当前没有可用的技能。"));

        var lines = skills.Select(s => $"- {s.Name}: {s.Description}");
        return Task.FromResult(Ok(string.Join("\n", lines)));
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
