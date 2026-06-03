using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.QuickActions;

[ToolMeta(Group = null, ContinueLoop = true, OutputOnly = true)]
public class SetGroupCardTool : ITool
{
    private readonly IAdapterAccess? _adapterAccess;
    private readonly string _adapterId = "";

    public SetGroupCardTool() { }

    public SetGroupCardTool(IAdapterAccess adapterAccess, string adapterId)
    {
        _adapterAccess = adapterAccess;
        _adapterId = adapterId;
    }

    public string Name => "set_group_card";
    public string Description => "修改群成员名片（群昵称）。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("group_id", "群号", 0),
        new("user_id", "目标用户QQ号", 1),
        new("card", "新名片内容", 2)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (_adapterAccess == null)
            return new ToolResult { Status = "failed", Error = "适配器服务不可用" };

        var groupId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var userId = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
        var card = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";

        if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(card))
            return new ToolResult { Status = "failed", Error = "group_id、user_id、card 不能为空" };

        var paramJson = $"{{\"group_id\":\"{groupId}\",\"user_id\":\"{userId}\",\"card\":\"{card}\"}}";
        var result = await _adapterAccess.ExecuteActionAsync(_adapterId, "set_group_card", paramJson);
        return result != null
            ? new ToolResult { Status = "success", Data = $"已将 {userId} 的群名片改为 {card}" }
            : new ToolResult { Status = "failed", Error = "修改群名片失败" };
    }
}
