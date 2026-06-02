using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.WorkingTools;

[ToolMeta(Group = "working", ExpressAvailable = false)]
public class MarkForReviewTool : ITool
{
    private readonly IBeaconAccess _beacon;

    public MarkForReviewTool(IBeaconAccess beacon)
    {
        _beacon = beacon;
    }

    public string Name => "mark_for_review";
    public string Description => "标记当前对话位置为复盘信标。下次深度睡眠时复盘模块会关注这里。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("reason", "标记原因（为什么值得复盘关注）", 0),
        new("channel_id", "内部频道ID（见频道信息 频道ID）", 1, false),
        new("person_id", "内部人物ID（见参与者列表 person_id）", 2, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var reason = inputs.Count > 0 ? inputs[0] : null;
        if (string.IsNullOrWhiteSpace(reason))
            return new ToolResult { Status = "failed", Error = "reason 不能为空" };

        int? channelId = inputs.Count > 1 && int.TryParse(inputs[1], out var cid) ? cid : null;
        int? personId = inputs.Count > 2 && int.TryParse(inputs[2], out var pid) ? pid : null;

        await _beacon.CreateAsync(reason, source: "model", consumer: "review",
            channelId: channelId, personId: personId);

        return new ToolResult { Status = "success", Data = "已标记为复盘信标。" };
    }
}
