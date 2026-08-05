using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

/// <summary>
/// 认领并处理队列项工具。认领单个待验证项（Pending → UnderReview）。
/// 一次只认领一个，专注解决单个问题。认领后由模型查证 → 调锚点进暂存区 → 预览 → 提交。
/// </summary>
[ToolMeta(Group = "review")]
public class ReviewResolveTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewResolveTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_resolve";
    public string Description => "认领并处理一条待验证队列项（一次一个）。认领成功后进入 UnderReview，请 review_evidence 查证后 review_preview 预览改动、review_commit 提交。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("queue_id", "待验证队列项 Id（review_list 获得）", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        // 服务取不到时返回 failed 并说明（不用 Require 抛异常）
        var queue = _ctx.GetService<IReviewQueueAccess>();
        if (queue == null)
            return new ToolResult { Status = "failed", Error = "待验证队列服务（IReviewQueueAccess）不可用，请确认 Mind 模块已初始化。" };

        if (inputs.Count == 0 || !int.TryParse(inputs[0], out var queueId))
            return new ToolResult { Status = "failed", Error = "queue_id 不能为空，且必须为整数。" };

        var item = await queue.ClaimAsync(queueId);
        if (item == null)
            return new ToolResult { Status = "failed", Error = $"认领队列项 #{queueId} 失败：项不存在或已被认领。" };

        return new ToolResult
        {
            Status = "success",
            Data = $"已认领 {item.TargetKind}:{item.TargetRef}，请 review_evidence 查证后 review_preview 预览改动。"
        };
    }
}
