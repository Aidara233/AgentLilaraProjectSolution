using System.Text;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

/// <summary>
/// 查证目标断言工具（支持/反对边 + 子证据）。
/// 按 claim_id 检索该断言，返回相关记忆（Claim/Journal 混合）与命中断言的子证据（二跳内支持/反对边 Raw）。
/// 推断的断言标注 [推断]，不得与既定事实混同（05 模型 2.1）。
/// </summary>
[ToolMeta(Group = "review")]
public class ReviewEvidenceTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewEvidenceTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_evidence";
    public string Description =>
        "查证目标断言（支持/反对边 + 子证据）。按 claim_id 检索该断言，返回相关记忆及命中断言的子证据；" +
        "未命中时提示改用 remember 以表面词检索。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("claim_id", "目标断言 Id（review_list 队列项的 TargetRef）", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        // 服务取不到时返回 failed 并说明（不用 Require 抛异常）
        var retrieve = _ctx.GetService<IRetrieveAccess>();
        if (retrieve == null)
            return new ToolResult { Status = "failed", Error = "检索服务（IRetrieveAccess）不可用，请确认 RetrieveService 模块已初始化。" };

        if (inputs.Count == 0 || !int.TryParse(inputs[0], out var claimId))
            return new ToolResult { Status = "failed", Error = "claim_id 不能为空，且必须为整数。" };

        // query 用 claim_id（或由模型在描述中引导用表面词）；命中即带出子证据
        var query = claimId.ToString();
        var result = await retrieve.SearchAsync(query, 20);
        if (result.Hits.Count == 0)
            return new ToolResult
            {
                Status = "success",
                Data = $"未检索到断言 #{claimId} 的相关记忆。可尝试用 remember 以该断言的表面词（主语/宾语）检索。"
            };

        var sb = new StringBuilder();
        sb.AppendLine($"检索「{result.Query}」命中 {result.Hits.Count} 条：");
        foreach (var h in result.Hits)
        {
            // 命中目标断言时标记；推断断言显式标注 [推断]
            var target = h.Kind == "Claim" && h.Id == claimId ? " ← 目标断言" : "";
            var inference = h.IsInference ? " [推断]" : "";
            var meta = h.Kind == "Journal"
                ? $" (命中{h.MatchCount})"
                : $" (credibility={h.Credibility:F2}, 命中{h.MatchCount})";

            sb.AppendLine($"[{h.Kind}#{h.Id}]{inference}{target}{meta}");
            sb.AppendLine($"  {h.Raw ?? "(无原文)"}");

            if (h.SubEvidence.Count > 0)
            {
                sb.AppendLine("  子证据（支持/反对边）：");
                foreach (var ev in h.SubEvidence)
                    sb.AppendLine($"    - {ev}");
            }
        }
        return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
    }
}
