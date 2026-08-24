using AgentLilara.PluginSDK;

namespace Plugin.SkillTools;

[Component(Name = "skill-tools", Scope = ComponentScope.Global)]
public class SkillToolsComponent : GlobalComponentBase
{
    private readonly Dictionary<string, SkillEntry> _skills = new(StringComparer.OrdinalIgnoreCase);
    private string _skillsDir = null!;

    private InvokeSkillTool? _invokeSkill;
    private LoadSkillFileTool? _loadFile;
    private ListSkillsTool? _listSkills;
    private ListSkillFilesTool? _listFiles;

    public override ComponentMeta Meta => new()
    {
        Name = "skill-tools",
        Description = "技能系统：按需加载预定义技能指导",
        DefaultEnabled = true,
        PromptPriority = 20
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_invokeSkill != null) yield return _invokeSkill;
            if (_loadFile != null) yield return _loadFile;
            if (_listSkills != null) yield return _listSkills;
            if (_listFiles != null) yield return _listFiles;
        }
    }

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _skillsDir = Path.Combine(context.Storage.GlobalDirectory, "Skills");
        Directory.CreateDirectory(_skillsDir);
        ScanSkills();

        Func<string, SkillEntry?> getSkill = name =>
            _skills.TryGetValue(name, out var s) ? s : null;
        Func<IReadOnlyList<SkillEntry>> getSkills = () => _skills.Values.ToList();

        _invokeSkill = new InvokeSkillTool(getSkill);
        _loadFile = new LoadSkillFileTool(getSkill);
        _listSkills = new ListSkillsTool(getSkills);
        _listFiles = new ListSkillFilesTool(getSkill);

        return Task.CompletedTask;
    }

    /// <summary>每轮注入返回 null：技能清单是静态参考，只在会话开头注入一次（BuildStartPromptSection），
    /// 避免每个突发都重推整份清单、且历史里堆叠 [组件通知] 副本。</summary>
    public override string? BuildPromptSection(LoopInfo caller)
    {
        _ = caller;
        return null;
    }

    /// <summary>会话开头一次性注入技能清单，让模型知道有哪些技能可 invoke_skill。</summary>
    public override string? BuildStartPromptSection(LoopInfo caller)
    {
        _ = caller;
        if (_skills.Count == 0)
            return null;

        var lines = _skills.Values.Select(s => $"  - {s.Name}: {s.Description}");
        return $"可用技能（通过 invoke_skill 调用）:\n{string.Join("\n", lines)}";
    }

    private void ScanSkills()
    {
        _skills.Clear();

        if (!Directory.Exists(_skillsDir))
            return;

        foreach (var dir in Directory.GetDirectories(_skillsDir))
        {
            var entry = SkillEntry.Parse(dir);
            if (entry != null)
                _skills[entry.Name] = entry;
        }
    }
}
