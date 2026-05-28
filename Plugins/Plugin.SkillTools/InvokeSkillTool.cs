using AgentLilara.PluginSDK;

namespace Plugin.SkillTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "技能：调用预定义技能获取指导内容")]
public class InvokeSkillTool : ITool
{
    private readonly Func<string, SkillEntry?> _getSkill;

    public InvokeSkillTool(Func<string, SkillEntry?> getSkill)
    {
        _getSkill = getSkill;
    }

    public string Name => "invoke_skill";
    public string Description => "调用指定技能，返回其完整指导内容。";
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

        try
        {
            var body = skill.GetBody();
            return Task.FromResult(Ok(body));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail($"读取技能失败: {ex.Message}"));
        }
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
