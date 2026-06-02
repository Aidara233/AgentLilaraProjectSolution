using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[Component(Name = "review-tools", Scope = ComponentScope.Global)]
[LoopApplicability(Channel = Applicability.NotApplicable, System = Applicability.NotApplicable)]
public class ReviewToolsComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "review-tools",
        Description = "复盘工具集（游标浏览、记忆搜索、评价、人物更新、进度管理）",
        DefaultEnabled = true,
        PromptPriority = 90
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        var toolCtx = context.GetService<IToolContext>();
        if (toolCtx == null) return Task.CompletedTask;

        // 导航组
        _tools.Add(new ReviewFocusTool(toolCtx));
        _tools.Add(new ReviewBrowseTool(toolCtx));
        _tools.Add(new ReviewSearchMessagesTool(toolCtx));
        _tools.Add(new ReviewGetPersonTool(toolCtx));
        _tools.Add(new ReviewListBeaconsTool(context.GetService<IBeaconAccess>()!));

        // 行动组
        _tools.Add(new ReviewUpdatePersonTool(toolCtx));
        _tools.Add(new ReviewEvaluateTool(toolCtx));

        // 元工具组
        _tools.Add(new ReviewThinkingNotesTool(toolCtx));
        _tools.Add(new ReviewSaveProgressTool(toolCtx));
        _tools.Add(new ReviewRequestReinforcementTool(toolCtx));
        _tools.Add(new ReviewLogTool(toolCtx));
        _tools.Add(new ReviewCompleteTool(toolCtx));

        return Task.CompletedTask;
    }
}
