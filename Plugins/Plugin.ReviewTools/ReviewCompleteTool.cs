using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewCompleteTool : ITool
{
    private readonly IReviewControl _control;

    public ReviewCompleteTool(IToolContext ctx)
    {
        _control = ctx.Require<IReviewControl>();
    }

    public string Name => "review_complete";
    public string Description => "标记复盘完成。调用后复盘引擎将退出。请确保已保存所有发现。";
    public IReadOnlyList<ToolParameter> Parameters => [];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        _control.MarkComplete();
        return Task.FromResult(new ToolResult { Status = "success", Data = "复盘已标记完成。" });
    }
}
