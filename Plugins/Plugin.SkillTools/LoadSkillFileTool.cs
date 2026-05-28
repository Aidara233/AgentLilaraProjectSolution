using AgentLilara.PluginSDK;

namespace Plugin.SkillTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "技能：加载技能的辅助文件")]
public class LoadSkillFileTool : ITool
{
    private readonly Func<string, SkillEntry?> _getSkill;

    public LoadSkillFileTool(Func<string, SkillEntry?> getSkill)
    {
        _getSkill = getSkill;
    }

    public string Name => "load_skill_file";
    public string Description => "加载指定技能的辅助文件内容。先用 list_skill_files 查看可用文件。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("skill_name", "技能名称", 0),
        new("file_name", "文件名", 1)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var skillName = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var fileName = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

        if (string.IsNullOrWhiteSpace(skillName))
            return Task.FromResult(Fail("请提供技能名称"));
        if (string.IsNullOrWhiteSpace(fileName))
            return Task.FromResult(Fail("请提供文件名"));

        var skill = _getSkill(skillName);
        if (skill == null)
            return Task.FromResult(Fail($"未找到技能: {skillName}"));

        try
        {
            var content = skill.ReadFile(fileName);
            if (content == null)
                return Task.FromResult(Fail($"技能 {skillName} 中不存在文件: {fileName}"));

            return Task.FromResult(Ok(content));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail($"读取文件失败: {ex.Message}"));
        }
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
