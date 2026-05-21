using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewWriteMemoryTool : ITool
{
    private readonly IMemoryAccess _memory;
    private readonly IReviewAccess _review;

    public ReviewWriteMemoryTool(IToolContext ctx)
    {
        _memory = ctx.Require<IMemoryAccess>();
        _review = ctx.Require<IReviewAccess>();
    }

    public string Name => "review_write_memory";
    public string Description => "写入记忆。写入前请先 review_search_memory 确认无重复或高度相似的记忆。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("content", "记忆内容", 0),
        new("importance", "重要度 0.0~1.0（默认0.5）", 1, false),
        new("person_id", "关联人物ID", 2, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var content = inputs.Count > 0 ? inputs[0] : null;
        if (string.IsNullOrWhiteSpace(content))
            return new ToolResult { Status = "failed", Error = "content 不能为空" };

        float importance = inputs.Count > 1 && float.TryParse(inputs[1], out var imp) ? imp : 0.5f;
        int? personId = inputs.Count > 2 && int.TryParse(inputs[2], out var pid) ? pid : null;

        var id = await _memory.StoreAsync(new MemoryWriteRequest
        {
            Content = content,
            Importance = importance,
            PersonId = personId
        });

        var detail = System.Text.Json.JsonSerializer.Serialize(new { memoryId = id, content, importance, personId });
        await _review.LogActionAsync("write_memory", $"写入记忆: {content[..Math.Min(content.Length, 40)]}", detail);

        return new ToolResult { Status = "success", Data = $"记忆已写入 (ID:{id})" };
    }
}
