using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.WorkingTools;

/// <summary>
/// 标记当前对话位置为待验证项（Manual 来源）。后端走 ReviewQueue（取代旧信标）。
/// </summary>
[ToolMeta(Group = "working")]
public class MarkForReviewTool : ITool
{
    private readonly IReviewQueueAccess _queue;

    public MarkForReviewTool(IReviewQueueAccess queue)
    {
        _queue = queue;
    }

    public string Name => "mark_for_review";
    public string Description => "标记当前对话内容为待验证。Review 验证会话会关注这里并核实。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("reason", "标记原因（为什么值得验证）", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var reason = inputs.Count > 0 ? inputs[0] : null;
        if (string.IsNullOrWhiteSpace(reason))
            return new ToolResult { Status = "failed", Error = "reason 不能为空" };

        // 入队（Source=Manual，Normal 优先级）。TargetRef 用 reason（无具体断言时按文本标记）。
        await _queue.EnqueueAsync(reason, "Manual", reason, "Manual", "Normal");
        return new ToolResult { Status = "success", Data = "已标记为待验证项。" };
    }
}
