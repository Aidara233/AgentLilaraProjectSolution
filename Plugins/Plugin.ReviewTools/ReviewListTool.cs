using System.Text;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

/// <summary>
/// 查看待验证队列工具。列出按优先级排队的待验证项（Pending）。
/// 队列是 Review 唯一的待处理清单，各项包含目标类型/目标引用/优先级/来源/原因。
/// </summary>
[ToolMeta(Group = "review")]
public class ReviewListTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewListTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_list";
    public string Description => "查看待验证队列，列出待处理项（Id/目标类型/目标引用/优先级/来源/原因）。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("limit", "（可选）最大返回条数，默认 10", 0, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        // 服务取不到时返回 failed 并说明（不用 Require 抛异常）
        var queue = _ctx.GetService<IReviewQueueAccess>();
        if (queue == null)
            return new ToolResult { Status = "failed", Error = "待验证队列服务（IReviewQueueAccess）不可用，请确认 Mind 模块已初始化。" };

        var limit = inputs.Count > 0 && int.TryParse(inputs[0], out var l) ? l : 10;
        if (limit < 1) limit = 10;

        var items = await queue.GetPendingAsync(limit);
        if (items.Count == 0)
            return new ToolResult { Status = "success", Data = "待验证队列为空。" };

        var sb = new StringBuilder();
        sb.AppendLine($"待验证队列（{items.Count} 项）：");
        foreach (var it in items)
        {
            sb.AppendLine($"[#{it.Id}] {it.TargetKind}:{it.TargetRef} | 优先级={it.Priority} | 来源={it.Source}");
            if (!string.IsNullOrWhiteSpace(it.Reason))
                sb.AppendLine($"  原因：{it.Reason}");
        }
        return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
    }
}
