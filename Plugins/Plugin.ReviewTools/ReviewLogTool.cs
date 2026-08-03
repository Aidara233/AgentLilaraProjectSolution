using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewLogTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewLogTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_log";
    public string Description => "记录一次复盘审计日志。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("action", "动作类型，如 write_memory / link_memory / evaluate / update_person", 0),
        new("summary", "动作摘要", 1),
        new("detail", "（可选）详细信息 JSON", 2, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var review = _ctx.Require<IReviewAccess>();
        if (inputs.Count < 2)
            return new ToolResult { Status = "failed", Error = "action 和 summary 不能为空" };

        var action = inputs[0].Trim();
        var summary = inputs[1].Trim();
        var detail = inputs.Count > 2 && !string.IsNullOrWhiteSpace(inputs[2]) ? inputs[2].Trim() : null;

        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(summary))
            return new ToolResult { Status = "failed", Error = "action 和 summary 不能为空" };

        await review.LogActionAsync(action, summary, detail);
        return new ToolResult { Status = "success", Data = $"已记录: {action}" };
    }
}
