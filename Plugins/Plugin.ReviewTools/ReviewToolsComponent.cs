using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[Component(Name = "review-tools", Scope = ComponentScope.Global)]
[LoopApplicability(Channel = Applicability.NotApplicable, System = Applicability.NotApplicable)]
[ToolVisibility(Default = Visibility.AlwaysVisible)]
public class ReviewToolsComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "review-tools",
        Description = "复盘工具集（记忆搜索、消息阅读、人物更新、进度管理）",
        DefaultEnabled = true,
        PromptPriority = 90
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        var toolCtx = context.GetService<IToolContext>();
        if (toolCtx == null) return Task.CompletedTask;

        _tools.Add(new ReviewSearchMemoryTool(toolCtx));
        _tools.Add(new ReviewReadMessagesTool(toolCtx));
        _tools.Add(new ReviewViewLinksTool(toolCtx));
        _tools.Add(new ReviewWriteMemoryTool(toolCtx));
        _tools.Add(new ReviewUpdatePersonTool(toolCtx));
        _tools.Add(new ReviewUpdateAffinityTool(toolCtx));
        _tools.Add(new ReviewThinkingNotesTool());
        _tools.Add(new ReviewSaveProgressTool(toolCtx));
        _tools.Add(new ReviewRequestReinforcementTool(toolCtx));
        _tools.Add(new ReviewCompleteTool(toolCtx));

        return Task.CompletedTask;
    }
}
