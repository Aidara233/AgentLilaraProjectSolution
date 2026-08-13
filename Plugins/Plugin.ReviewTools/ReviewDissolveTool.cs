using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

/// <summary>
/// 拆解冲突组工具（个例处理）。
/// 当这一组其实不构成冲突（两个取值可以共存、或时效不同其实不矛盾），标记该组已解决，
/// 组内可信度预算约束解除。改动进暂存区，需 review_commit 提交。
/// 与 review_declassify 的区别：本工具只处理当前这一组，属性仍保持排他；
/// 若该属性本来就不该排他，用 review_declassify 治本。
/// </summary>
[ToolMeta(Group = "review")]
public class ReviewDissolveTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewDissolveTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_dissolve";
    public string Description =>
        "拆解一个冲突组（标记已解决，组内可信度预算约束解除）。" +
        "用于这一组其实不矛盾的情况——两个取值可共存，或时效不同各自成立。" +
        "属性仍保持排他；若该属性本来就该多值，改用 review_declassify。改动进暂存区，需 review_commit 提交。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("group_id", "冲突组 Id（队列项 TargetKind=ConflictGroup 时的 TargetRef）", 0)
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

        if (inputs.Count == 0 || !int.TryParse(inputs[0], out var groupId))
            return Task.FromResult(new ToolResult
            {
                Status = "failed",
                Error = "group_id 不能为空，且必须为整数。"
            });

        stage.StageGroupResolution(groupId);

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"已暂存：拆解冲突组 #{groupId}。请 review_preview 确认后 review_commit。"
        });
    }
}
