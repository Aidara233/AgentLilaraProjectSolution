using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewGetLinksTool : ITool
{
    private readonly IMemoryAccess _memory;

    public ReviewGetLinksTool(IToolContext ctx) => _memory = ctx.Require<IMemoryAccess>();

    public string Name => "review_get_links";
    public string Description => "查看某条记忆的关联列表。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("memory_id", "记忆ID", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0 || !int.TryParse(inputs[0], out var memoryId))
            return new ToolResult { Status = "failed", Error = "memory_id 必须为整数" };

        var links = await _memory.GetLinkedAsync(memoryId);
        if (links.Count == 0)
            return new ToolResult { Status = "success", Data = $"记忆 ID:{memoryId} 没有关联。" };

        var lines = links.Select(l => $"[ID:{l.Id}] (重要度:{l.Importance:F1}) {l.Content}");
        return new ToolResult { Status = "success", Data = $"记忆 ID:{memoryId} 的关联:\n{string.Join("\n", lines)}" };
    }
}
