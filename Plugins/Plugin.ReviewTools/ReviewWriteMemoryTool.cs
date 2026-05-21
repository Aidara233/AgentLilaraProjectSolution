using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewWriteMemoryTool : ITool
{
    private readonly IMemoryAccess _memory;

    public ReviewWriteMemoryTool(IToolContext ctx)
    {
        _memory = ctx.Require<IMemoryAccess>();
    }

    public string Name => "review_write_memory";
    public string Description => "将复盘发现写入主记忆库。用于记录深度分析结论。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("content", "记忆内容", 0),
        new("importance", "重要度（0.0-1.0，默认0.6）", 1, false),
        new("person_id", "可选：关联人物ID", 2, false),
        new("subject", "可选：主题标签", 3, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var content = inputs.Count > 0 ? inputs[0] : null;
        if (string.IsNullOrWhiteSpace(content))
            return new ToolResult { Status = "failed", Error = "content 不能为空" };

        float importance = inputs.Count > 1 && float.TryParse(inputs[1], out var imp) ? imp : 0.6f;
        int? personId = inputs.Count > 2 && int.TryParse(inputs[2], out var pid) ? pid : null;
        string? subject = inputs.Count > 3 ? inputs[3] : null;

        var id = await _memory.StoreAsync(new MemoryWriteRequest
        {
            Content = content,
            Importance = Math.Clamp(importance, 0f, 1f),
            PersonId = personId,
            Subject = subject,
            Type = "review",
            Confidence = "high"
        });

        return new ToolResult { Status = "success", Data = $"已写入记忆 ID:{id}" };
    }
}
