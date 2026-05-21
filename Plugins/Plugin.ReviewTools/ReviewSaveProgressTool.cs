using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewSaveProgressTool : ITool
{
    private readonly IReviewControl _control;

    public ReviewSaveProgressTool(IToolContext ctx)
    {
        _control = ctx.Require<IReviewControl>();
    }

    public string Name => "review_save_progress";
    public string Description => "保存当前调查进度。下次大睡时可从此处继续。包含发现和待完成步骤。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("findings", "已有发现（JSON数组或换行分隔）", 0),
        new("next_steps", "待完成步骤（JSON数组或换行分隔）", 1, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var findings = inputs.Count > 0 ? inputs[0] : "";
        var nextSteps = inputs.Count > 1 ? inputs[1] : "";

        var progress = System.Text.Json.JsonSerializer.Serialize(new
        {
            findings,
            next_steps = nextSteps,
            saved_at = DateTime.UtcNow
        });

        _control.SaveProgress(progress);
        return Task.FromResult(new ToolResult { Status = "success", Data = "进度已保存。" });
    }
}
