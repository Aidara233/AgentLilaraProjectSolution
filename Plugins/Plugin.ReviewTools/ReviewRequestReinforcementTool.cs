using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewRequestReinforcementTool : ITool
{
    private readonly IReviewControl _control;

    public ReviewRequestReinforcementTool(IToolContext ctx)
    {
        _control = ctx.Require<IReviewControl>();
    }

    public string Name => "review_request_reinforcement";
    public string Description => "请求备用 token 预算。仅可使用一次，系统即将醒来时不可用。";
    public IReadOnlyList<ToolParameter> Parameters => [];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var granted = _control.RequestReinforcement();
        return Task.FromResult(granted
            ? new ToolResult { Status = "success", Data = "备用预算已批准，可继续工作。" }
            : new ToolResult { Status = "failed", Error = "备用预算不可用（已使用过或系统即将醒来）。" });
    }
}
