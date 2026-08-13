using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

/// <summary>
/// 解除属性排他工具（根因修复）。
/// 当冲突其实是误标——该属性本来就允许多值共存（如"爱好"、"技能"）——解除它的排他标记，
/// 今后同主语该属性的不同取值不再建冲突组。改动进暂存区，需 review_commit 提交。
/// 与 review_dissolve 的区别：本工具治本（该属性今后都不冲突），dissolve 只解决当前这一组。
/// </summary>
[ToolMeta(Group = "review")]
public class ReviewDeclassifyTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewDeclassifyTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_declassify";
    public string Description =>
        "解除某个属性的排他标记（该属性今后允许多值共存，不再触发冲突组）。" +
        "用于冲突是误标的情况——属性本身就该允许多值（爱好/技能/去过的地方）。" +
        "改动进暂存区，需 review_commit 提交。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("attribute", "属性名（冲突组的 PredicateKey，如「爱好」）", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var stage = _ctx.GetService<IReviewStageAccess>();
        if (stage == null)
            return Task.FromResult(new ToolResult
            {
                Status = "failed",
                Error = "暂存区服务（IReviewStageAccess）不可用，仅 review 会话提供。"
            });

        if (inputs.Count == 0 || string.IsNullOrWhiteSpace(inputs[0]))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "attribute 不能为空。" });

        var attribute = inputs[0].Trim();
        stage.StageExclusivityRemoval(attribute);

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"已暂存：解除属性「{attribute}」的排他标记。" +
                   "提交后同主语该属性的不同取值不再建冲突组。请 review_preview 确认后 review_commit。"
        });
    }
}
