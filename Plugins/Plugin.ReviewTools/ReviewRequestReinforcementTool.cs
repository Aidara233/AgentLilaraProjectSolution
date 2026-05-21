using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewRequestReinforcementTool : ITool
{
    private readonly IReviewControl _control;

    public ReviewRequestReinforcementTool(IToolContext ctx) => _control = ctx.Require<IReviewControl>();

    public string Name => "review_request_reinforcement";
    public string Description => "请求备用预算（仅一次）。系统醒来后不可用。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (_control.WakeNotified)
            return Task.FromResult(new ToolResult { Status = "failed", Error = "系统已醒来，备用预算不可用。" });

        if (_control.ReserveGranted)
            return Task.FromResult(new ToolResult { Status = "failed", Error = "备用预算已使用过，不可重复申请。" });

        var success = _control.RequestReinforcement();
        return Task.FromResult(success
            ? new ToolResult { Status = "success", Data = "备用预算已激活。上限已扩展。" }
            : new ToolResult { Status = "failed", Error = "申请失败。" });
    }
}
