using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewRemoveBeaconTool : ITool
{
    private readonly IBeaconAccess _beacon;

    public ReviewRemoveBeaconTool(IBeaconAccess beacon) => _beacon = beacon;

    public string Name => "review_remove_beacon";
    public string Description => "标记指定信标为已处理（不再出现在待处理列表中）。参数为信标 ID（从 review_list_beacons 获取）。";
    public IReadOnlyList<ToolParameter> Parameters => [new("beacon_id", "要移除的信标 ID", 0)];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0 || !int.TryParse(inputs[0], out var id))
            return new ToolResult { Status = "failed", Error = "beacon_id 必须为整数" };

        await _beacon.MarkProcessedAsync(id);
        return new ToolResult { Status = "success", Data = $"信标 #{id} 已标记为已处理。" };
    }
}
