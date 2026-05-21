using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewViewLinksTool : ITool
{
    private readonly IMemoryAccess _memory;

    public ReviewViewLinksTool(IToolContext ctx)
    {
        _memory = ctx.Require<IMemoryAccess>();
    }

    public string Name => "review_view_links";
    public string Description => "查看指定记忆的关联记忆列表。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("memory_id", "记忆ID", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0 || !int.TryParse(inputs[0], out var memoryId))
            return new ToolResult { Status = "failed", Error = "memory_id 必须是有效整数" };

        var linked = await _memory.GetLinkedAsync(memoryId);
        if (linked.Count == 0)
            return new ToolResult { Status = "success", Data = "该记忆无关联条目。" };

        var lines = linked.Select(m =>
            $"[ID:{m.Id}] (重要度:{m.Importance:F1}) {m.Content}");
        return new ToolResult { Status = "success", Data = string.Join("\n", lines) };
    }
}
