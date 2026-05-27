using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.QuickActions;

[ToolMeta(Group = null, ContinueLoop = true, ExpressAvailable = true, OutputOnly = true)]
public class RecallTool : ITool
{
    private readonly IAdapterAccess? _adapterAccess;
    private readonly string _adapterId = "";

    public RecallTool() { }

    public RecallTool(IAdapterAccess adapterAccess, string adapterId)
    {
        _adapterAccess = adapterAccess;
        _adapterId = adapterId;
    }

    public string Name => "recall";
    public string Description => "撤回指定消息（仅限机器人自己发送的消息）。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("message_id", "要撤回的消息ID", 0)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (_adapterAccess == null)
            return new ToolResult { Status = "failed", Error = "适配器服务不可用" };

        var messageId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        if (string.IsNullOrEmpty(messageId))
            return new ToolResult { Status = "failed", Error = "message_id 不能为空" };

        var paramJson = $"{{\"message_id\":\"{messageId}\"}}";
        var result = await _adapterAccess.ExecuteActionAsync(_adapterId, "recall", paramJson);
        return result != null
            ? new ToolResult { Status = "success", Data = $"已撤回消息 {messageId}" }
            : new ToolResult { Status = "failed", Error = "撤回失败（可能超时或权限不足）" };
    }
}
