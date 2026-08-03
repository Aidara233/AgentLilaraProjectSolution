using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[ToolMeta(ContinueLoop = true)]
public class ListLoopsTool : ITool
{
    private readonly IAgentMessaging _messaging;

    public ListLoopsTool(IAgentMessaging messaging) => _messaging = messaging;

    public string Name => "list_loops";
    public string Description => "列出当前所有活跃循环的 ID。";

    public IReadOnlyList<ToolParameter> Parameters => [];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var loopIds = _messaging.GetActiveLoopIds();

        if (loopIds.Count == 0)
            return Task.FromResult(new ToolResult { Status = "success", Data = "没有已知的活跃循环。" });

        var lines = loopIds.OrderBy(id =>
        {
            var t = id.Contains(':') ? id[..id.IndexOf(':')] : id;
            return t switch { "channel" => "1", "task" => "2", _ => "9" } + id;
        }).Select(id => $"  {id}");

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"活跃循环（共 {loopIds.Count} 个）:\n" + string.Join("\n", lines)
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
