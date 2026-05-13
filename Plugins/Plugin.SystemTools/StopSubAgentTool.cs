// Plugins/Plugin.SystemTools/StopSubAgentTool.cs
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "停止子agent")]
public class StopSubAgentTool : ITool
{
    private readonly ISubAgentAccess _subAgents;

    public StopSubAgentTool(ISubAgentAccess subAgents)
    {
        _subAgents = subAgents;
    }

    public string Name => "stop_sub_agent";
    public string Description => "请求停止指定的子agent会话。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("session_id", "子agent会话ID", 0)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var sessionId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;

        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "session_id 不能为空" });

        var info = _subAgents.Get(sessionId);
        if (info == null)
            return Task.FromResult(new ToolResult { Status = "failed", Error = $"子agent {sessionId} 不存在" });

        _subAgents.RequestStop(sessionId);

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"已请求停止子agent {sessionId}。"
        });
    }

    public JsonNode GetInputSchema()
    {
        return JsonNode.Parse("""
        {
            "type": "object",
            "properties": {
                "session_id": { "type": "string", "description": "子agent会话ID" }
            },
            "required": ["session_id"]
        }
        """)!;
    }
}
