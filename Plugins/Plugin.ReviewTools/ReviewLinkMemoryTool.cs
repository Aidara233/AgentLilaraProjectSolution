using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewLinkMemoryTool : ITool
{
    private readonly IMemoryAccess _memory;
    private readonly IReviewAccess _review;

    public ReviewLinkMemoryTool(IToolContext ctx)
    {
        _memory = ctx.Require<IMemoryAccess>();
        _review = ctx.Require<IReviewAccess>();
    }

    public string Name => "review_link_memory";
    public string Description => "创建或删除记忆关联。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("memory_id_a", "记忆A的ID", 0),
        new("memory_id_b", "记忆B的ID", 1),
        new("action", "操作: create 或 delete", 2)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (inputs.Count < 3)
            return new ToolResult { Status = "failed", Error = "需要 memory_id_a, memory_id_b, action 三个参数" };

        if (!int.TryParse(inputs[0], out var idA) || !int.TryParse(inputs[1], out var idB))
            return new ToolResult { Status = "failed", Error = "memory_id 必须为整数" };

        var action = inputs[2];
        if (action != "create" && action != "delete")
            return new ToolResult { Status = "failed", Error = "action 必须为 create 或 delete" };

        if (action == "create")
            await _memory.LinkAsync(idA, idB);
        else
            await _memory.UnlinkAsync(idA, idB);

        var summary = $"{action} 关联: {idA} ↔ {idB}";
        var detail = System.Text.Json.JsonSerializer.Serialize(new { memoryIdA = idA, memoryIdB = idB, action });
        await _review.LogActionAsync("link_memory", summary, detail);

        return new ToolResult { Status = "success", Data = summary };
    }
}
