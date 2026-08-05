using System.Text;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

/// <summary>
/// 显式提交暂存区工具（git 式提交）。
/// 全部暂存改动原子事务落库 + 证据引擎重算 c_actual + 校验组内和 ≤1；校验失败不落库并返回超预算组。
/// </summary>
[ToolMeta(Group = "review")]
public class ReviewCommitTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewCommitTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_commit";
    public string Description => "显式提交暂存区的可信度改动（原子事务落库 + 重算 + 校验组内和≤1）。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        // 服务取不到时返回 failed 并说明（不用 Require 抛异常）
        var stage = _ctx.GetService<IReviewStageAccess>();
        if (stage == null)
            return new ToolResult { Status = "failed", Error = "暂存区服务（IReviewStageAccess）不可用，仅 review 会话提供。" };

        var commit = await stage.CommitAsync();
        if (!commit.Success)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"提交失败：{commit.Error}");
            if (commit.OverBudgetGroups.Count > 0)
                sb.AppendLine($"超预算冲突组：{string.Join(", ", commit.OverBudgetGroups)}");
            return new ToolResult { Status = "failed", Error = sb.ToString().TrimEnd() };
        }

        return new ToolResult { Status = "success", Data = $"已提交 {commit.CommittedChanges} 条可信度改动。" };
    }
}
