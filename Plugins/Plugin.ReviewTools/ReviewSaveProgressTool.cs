using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewSaveProgressTool : ITool
{
    private readonly IToolContext _ctx;

    public ReviewSaveProgressTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "review_save_progress";
    public string Description => "保存当前进度（游标、评价缓冲、笔记）。下次大睡时可从此处继续。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var review = _ctx.Require<IReviewAccess>();
        review.SaveProgress();
        return Task.FromResult(new ToolResult { Status = "success", Data = "进度已保存。下次大睡时将从当前位置继续。" });
    }
}
