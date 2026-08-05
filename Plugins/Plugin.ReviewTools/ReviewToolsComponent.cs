using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

/// <summary>
/// 验证工具链组件（Global）。承载 review_list / review_resolve / review_preview / review_commit / review_evidence 工具。
/// 队列（IReviewQueueAccess）全局可用；暂存区（IReviewStageAccess）是 per-session 服务，
/// 由外部 ReviewOrchestrator 在 review 会话启动时注册，组件只注册工具、消费服务。
/// 工具归属由权限组白名单裁决，组件只管注册。
/// </summary>
[Component(Name = "review-tools", Scope = ComponentScope.Global)]
public class ReviewToolsComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "review-tools",
        Description = "验证工具链（队列 + 暂存提交）",
        DefaultEnabled = true,
        PromptPriority = 90
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        var toolCtx = context.GetService<IToolContext>();
        if (toolCtx == null) return Task.CompletedTask;

        _tools.Add(new ReviewListTool(toolCtx));
        _tools.Add(new ReviewResolveTool(toolCtx));
        _tools.Add(new ReviewPreviewTool(toolCtx));
        _tools.Add(new ReviewCommitTool(toolCtx));
        _tools.Add(new ReviewEvidenceTool(toolCtx));

        return Task.CompletedTask;
    }
}
