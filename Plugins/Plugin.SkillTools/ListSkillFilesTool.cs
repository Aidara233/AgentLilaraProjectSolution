using AgentLilara.PluginSDK;

namespace Plugin.SkillTools;

[ToolMeta(ContinueLoop = false)]
public class ListSkillFilesTool : ITool
{
    private readonly Func<string, SkillEntry?> _getSkill;

    public ListSkillFilesTool(Func<string, SkillEntry?> getSkill)
    {
        _getSkill = getSkill;
    }

    public string Name => "list_skill_files";
    public string Description => "列出指定技能目录中的辅助文件。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("skill_name", "技能名称", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var skillName = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        if (string.IsNullOrWhiteSpace(skillName))
            return Task.FromResult(Fail("请提供技能名称"));

        var skill = _getSkill(skillName);
        if (skill == null)
            return Task.FromResult(Fail($"未找到技能: {skillName}"));

        var files = skill.ListFiles();
        if (files.Count == 0)
            return Task.FromResult(Ok("该技能没有辅助文件。"));

        var lines = files.Select(f => $"- {f}");
        return Task.FromResult(Ok(string.Join("\n", lines)));
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
