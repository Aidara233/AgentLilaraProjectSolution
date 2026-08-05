using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MindTools;

/// <summary>
/// 心智工具组件（Global）。承载 journal/claim/remember/mind_status/archive 工具。
/// 工具归属由权限组白名单裁决（Storage/Session/{group}.json），组件只管注册。
/// </summary>
[Component(Name = "mind-tools", Scope = ComponentScope.Global)]
public class MindToolsComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "mind-tools",
        Description = "心智工具（journal/claim/remember/mind_status/archive）",
        DefaultEnabled = true,
        PromptPriority = 100,
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        var toolCtx = context.GetService<IToolContext>();
        if (toolCtx == null) return Task.CompletedTask;

        _tools.Add(new JournalTool(toolCtx));
        _tools.Add(new ClaimTool(toolCtx));
        _tools.Add(new RememberTool(toolCtx));
        _tools.Add(new MindStatusTool(toolCtx));
        _tools.Add(new ArchiveTool(toolCtx));

        return Task.CompletedTask;
    }
}
