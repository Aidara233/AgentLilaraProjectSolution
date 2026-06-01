using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewCompleteTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewCompleteTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_complete";
    public string Description => "标记复盘完成。评价缓冲将被应用，进度文件将被清除。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var control = _ctx.Require<IReviewControl>();
        var review = _ctx.Require<IReviewAccess>();
        control.MarkComplete();
        review.ClearProgress();
        return Task.FromResult(new ToolResult { Status = "success", Data = "复盘已完成。评价将在引擎关闭时应用。" });
    }
}
