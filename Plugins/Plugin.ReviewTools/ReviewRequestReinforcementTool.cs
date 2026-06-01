using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewRequestReinforcementTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewRequestReinforcementTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_request_reinforcement";
    public string Description => "请求备用预算（仅一次）。系统醒来后不可用。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var control = _ctx.Require<IReviewControl>();
        if (control.WakeNotified)
            return Task.FromResult(new ToolResult { Status = "failed", Error = "系统已醒来，备用预算不可用。" });

        if (control.ReserveGranted)
            return Task.FromResult(new ToolResult { Status = "failed", Error = "备用预算已使用过，不可重复申请。" });

        var success = control.RequestReinforcement();
        return Task.FromResult(success
            ? new ToolResult { Status = "success", Data = "备用预算已激活。上限已扩展。" }
            : new ToolResult { Status = "failed", Error = "申请失败。" });
    }
}
