using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewThinkingNotesTool : ITool
{
    private readonly IReviewAccess _review;

    public ReviewThinkingNotesTool(IToolContext ctx) => _review = ctx.Require<IReviewAccess>();

    public string Name => "review_thinking_notes";
    public string Description => "思考草稿，跨轮保留，压缩不丢。browse 的原始内容可能会被压缩，但 notes 始终保留。养成边读边记的习惯。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("action", "操作: read/append/clear", 0),
        new("content", "追加内容（append 时必填）", 1, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var action = inputs.Count > 0 ? inputs[0] : "read";

        switch (action)
        {
            case "read":
                var notes = _review.ThinkingNotes;
                return Task.FromResult(new ToolResult
                {
                    Status = "success",
                    Data = string.IsNullOrEmpty(notes) ? "（笔记为空）" : notes
                });

            case "append":
                var content = inputs.Count > 1 ? inputs[1] : null;
                if (string.IsNullOrWhiteSpace(content))
                    return Task.FromResult(new ToolResult { Status = "failed", Error = "append 需要 content 参数" });

                if (!string.IsNullOrEmpty(_review.ThinkingNotes))
                    _review.ThinkingNotes += "\n" + content;
                else
                    _review.ThinkingNotes = content;

                return Task.FromResult(new ToolResult { Status = "success", Data = "已追加到笔记。" });

            case "clear":
                _review.ThinkingNotes = "";
                return Task.FromResult(new ToolResult { Status = "success", Data = "笔记已清空。" });

            default:
                return Task.FromResult(new ToolResult { Status = "failed", Error = "action 必须为 read/append/clear" });
        }
    }
}
