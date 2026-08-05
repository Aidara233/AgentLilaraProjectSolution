using System.Text;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MindTools;

/// <summary>
/// 检索记忆工具：按关键词检索 Claims + Journals 混合结果。
/// 排序规则（检索服务侧）：命中关键词数降序 → 可信度降序 → 时间倒序。
/// 推断的断言召回时标注 [推断]，不得混同于既定事实（05 模型 2.1）。
/// </summary>
[ToolMeta(Group = "mind")]
public class RememberTool : ITool
{
    private readonly IToolContext _ctx;

    public RememberTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "remember";
    public string Description =>
        "按关键词检索记忆，返回混合结果（Claim/Journal）。" +
        "每条含类型、原文、可信度，推断的断言标 [推断]。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("query", "检索关键词（多个关键词用空格/标点分隔）", 0),
        new("limit", "（可选）最大返回条数，默认 20", 1, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        // 服务取不到时返回 failed 并说明（不用 Require 抛异常）
        var retrieve = _ctx.GetService<IRetrieveAccess>();
        if (retrieve == null)
            return new ToolResult { Status = "failed", Error = "检索服务（IRetrieveAccess）不可用，请确认 RetrieveService 模块已初始化。" };

        if (inputs.Count == 0 || string.IsNullOrWhiteSpace(inputs[0]))
            return new ToolResult { Status = "failed", Error = "query 不能为空。" };

        var query = inputs[0].Trim();
        var limit = inputs.Count > 1 && int.TryParse(inputs[1], out var l) ? l : 20;
        if (limit < 1) limit = 20;

        var result = await retrieve.SearchAsync(query, limit);
        if (result.Hits.Count == 0)
            return new ToolResult { Status = "success", Data = "未找到相关记忆。" };

        var sb = new StringBuilder();
        foreach (var h in result.Hits)
        {
            // 推断标记：推断断言必须显式标注（[推断]），不得与既定事实混同
            var inference = h.IsInference ? " [推断]" : "";

            // Journal 无可信度字段（恒 0），不展示；Claim 展示可信度与命中数
            var meta = h.Kind == "Journal"
                ? $" (命中{h.MatchCount})"
                : $" (credibility={h.Credibility:F2}, 命中{h.MatchCount})";

            sb.AppendLine($"[{h.Kind}#{h.Id}]{inference}{meta}");
            sb.AppendLine($"  {h.Raw ?? "(无原文)"}");
        }
        return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
    }
}
