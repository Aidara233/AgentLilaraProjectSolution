using System.Text;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

/// <summary>
/// 预览暂存区改动影响工具（dry-run，不写库）。
/// 引擎构造临时 batch（暂存改动 + 现有证据边）只算不写，输出"改动会导致哪些断言 c_actual 变动、哪些冲突组超预算"。
/// </summary>
[ToolMeta(Group = "review")]
public class ReviewPreviewTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewPreviewTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_preview";
    public string Description => "预览暂存区可信度改动的影响（dry-run，不写库）：改动会影响哪些断言、哪些冲突组超预算。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        // 服务取不到时返回 failed 并说明（不用 Require 抛异常）
        var stage = _ctx.GetService<IReviewStageAccess>();
        if (stage == null)
            return new ToolResult { Status = "failed", Error = "暂存区服务（IReviewStageAccess）不可用，仅 review 会话提供。" };

        var preview = await stage.PreviewAsync();
        if (!preview.Ok)
            return new ToolResult { Status = "failed", Error = preview.Message };

        var sb = new StringBuilder();
        sb.AppendLine(preview.Message);
        if (preview.AffectedClaims.Count > 0)
            sb.AppendLine($"受影响断言：{string.Join(", ", preview.AffectedClaims)}");
        if (preview.OverBudgetGroups.Count > 0)
            sb.AppendLine($"超预算冲突组：{string.Join(", ", preview.OverBudgetGroups)}");
        else
            sb.AppendLine("超预算冲突组：无");
        return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
    }
}
