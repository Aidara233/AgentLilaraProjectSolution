using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "列出活跃循环（仅系统循环可用）")]
public class ListLoopsTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public ListLoopsTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "list_loops";
    public string Description => "列出当前系统内所有活跃循环的 ID。仅系统循环可调用此工具。";

    public IReadOnlyList<ToolParameter> Parameters => [];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        // 通过 IAgentMessaging 获取所有活跃请求的发起者和目标来推断活跃循环
        // 实际的活跃循环列表由 DelegationBus.GetActiveLoopIds() 提供
        var active = _messaging.GetActiveRequests();
        var completed = _messaging.GetCompletedRequests();

        var loopIds = new HashSet<string>();
        foreach (var r in active)
        {
            loopIds.Add(r.InitiatorId);
            if (r.TargetId != null) loopIds.Add(r.TargetId);
            foreach (var resp in r.Responses)
                loopIds.Add(resp.ResponderId);
        }
        foreach (var r in completed)
        {
            loopIds.Add(r.InitiatorId);
            if (r.TargetId != null) loopIds.Add(r.TargetId);
            foreach (var resp in r.Responses)
                loopIds.Add(resp.ResponderId);
        }

        if (loopIds.Count == 0)
            return Task.FromResult(new ToolResult { Status = "success", Data = "没有已知的活跃循环。" });

        var lines = loopIds.OrderBy(id =>
        {
            var t = id.Contains(':') ? id[..id.IndexOf(':')] : id;
            return t switch { "system" => "0", "channel" => "1", "task" => "2", _ => "9" } + id;
        }).Select(id => $"  {id}");

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = "已知循环 ID:\n" + string.Join("\n", lines)
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {},
        "required": []
    }
    """)!;
}
