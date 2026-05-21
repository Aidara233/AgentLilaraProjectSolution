using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewCompleteTool : ITool
{
    private readonly IReviewControl _control;
    private readonly IReviewAccess _review;

    public ReviewCompleteTool(IToolContext ctx)
    {
        _control = ctx.Require<IReviewControl>();
        _review = ctx.Require<IReviewAccess>();
    }

    public string Name => "review_complete";
    public string Description => "标记复盘完成。评价缓冲将被应用，进度文件将被清除。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        _control.MarkComplete();
        _review.ClearProgress();
        return Task.FromResult(new ToolResult { Status = "success", Data = "复盘已完成。评价将在引擎关闭时应用。" });
    }
}
