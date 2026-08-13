using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

/// <summary>
/// 队列项收尾工具。把认领中的队列项标记 Done（已处理）或 Dropped（放弃/不需处理）。
/// 没有收尾动作时队列项会永远停在 UnderReview，直到崩溃恢复重置——队列因此积压。
/// </summary>
[ToolMeta(Group = "review")]
public class ReviewFinishTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewFinishTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_finish";
    public string Description =>
        "收尾一条队列项：action=done（已验证处理完）或 drop（无需处理/无法验证，放弃）。" +
        "处理完必须调用，否则该项停留在 UnderReview 阻塞队列。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("queue_id", "待验证队列项 Id", 0),
        new("action", "done（已处理）或 drop（放弃）", 1),
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var queue = _ctx.GetService<IReviewQueueAccess>();
        if (queue == null)
            return new ToolResult
            {
                Status = "failed",
                Error = "待验证队列服务（IReviewQueueAccess）不可用，请确认 Mind 模块已初始化。"
            };

        if (inputs.Count == 0 || !int.TryParse(inputs[0], out var queueId))
            return new ToolResult { Status = "failed", Error = "queue_id 不能为空，且必须为整数。" };

        var action = inputs.Count > 1 ? inputs[1].Trim().ToLowerInvariant() : "done";
        if (action is not ("done" or "drop"))
            return new ToolResult { Status = "failed", Error = $"action 只接受 done 或 drop，收到「{action}」。" };

        if (action == "done")
        {
            await queue.CompleteAsync(queueId);
            return new ToolResult { Status = "success", Data = $"队列项 #{queueId} 已标记完成。" };
        }

        await queue.DropAsync(queueId);
        return new ToolResult { Status = "success", Data = $"队列项 #{queueId} 已放弃。" };
    }
}
