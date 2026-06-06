using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.QuickActions;

[ToolMeta(Group = null, ContinueLoop = true, OutputOnly = true)]
public class PokeTool : ITool
{
    private readonly IAdapterAccess? _adapterAccess;
    private readonly string _adapterId = "";

    public PokeTool() { }

    public PokeTool(IAdapterAccess adapterAccess, string adapterId)
    {
        _adapterAccess = adapterAccess;
        _adapterId = adapterId;
    }

    public string Name => "poke";
    public string Description => "戳一戳指定用户（群聊需提供 group_id，私聊不需要）。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("user_id", "目标用户QQ号", 0),
        new("group_id", "群号（私聊不填）", 1, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (_adapterAccess == null)
            return new ToolResult { Status = "failed", Error = "适配器服务不可用" };

        var userId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        if (string.IsNullOrEmpty(userId))
            return new ToolResult { Status = "failed", Error = "user_id 不能为空" };

        var groupId = resolvedInputs.Count > 1 ? resolvedInputs[1]?.Trim() : null;

        var paramJson = string.IsNullOrEmpty(groupId)
            ? $"{{\"user_id\":\"{userId}\"}}"
            : $"{{\"user_id\":\"{userId}\",\"group_id\":\"{groupId}\"}}";

        var result = await _adapterAccess.ExecuteActionAsync(_adapterId, "poke", paramJson);
        return result != null
            ? new ToolResult { Status = "success", Data = $"已戳一戳 {userId}" }
            : new ToolResult { Status = "failed", Error = "戳一戳失败" };
    }
}
