using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewSaveProgressTool : ITool
{
    private readonly IReviewAccess _review;

    public ReviewSaveProgressTool(IToolContext ctx) => _review = ctx.Require<IReviewAccess>();

    public string Name => "review_save_progress";
    public string Description => "保存当前进度（游标、评价缓冲、笔记）。下次大睡时可从此处继续。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        _review.SaveProgress();
        return Task.FromResult(new ToolResult { Status = "success", Data = "进度已保存。下次大睡时将从当前位置继续。" });
    }
}
