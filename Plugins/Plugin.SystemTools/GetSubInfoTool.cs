// Plugins/Plugin.SystemTools/GetSubInfoTool.cs
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

/// <summary>
/// 查询子 agent 详细状态：轮次、存活时长、最终输出预览。
/// </summary>
[ToolMeta(ContinueLoop = true, CapabilitySummary = "查询子agent详情")]
public class GetSubInfoTool : ITool
{
    private readonly ISubAgentAccess _subAgents;

    public GetSubInfoTool(ISubAgentAccess subAgents) => _subAgents = subAgents;

    public string Name => "get_sub_info";
    public string Description =>
        "查询子 agent 的详细状态：sessionId、状态、轮次计数、" +
        "创建时间、最后活动时间、最终输出预览（已完成的子 agent）。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("session_id", "子 agent 的会话 ID", 0)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var sessionId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "session_id 不能为空" });

        var d = _subAgents.GetDetail(sessionId);
        if (d == null)
            return Task.FromResult(new ToolResult { Status = "failed", Error = $"子 agent {sessionId} 不存在" });

        var sb = new StringBuilder();
        sb.AppendLine($"- session_id: {d.SessionId}");
        sb.AppendLine($"- 状态: {d.Status} (alive={d.IsAlive})");
        if (!string.IsNullOrEmpty(d.ParentLoopId))
            sb.AppendLine($"- 父 loopId: {d.ParentLoopId}");
        sb.AppendLine($"- 轮次: {d.RoundCount}");
        var elapsed = System.DateTime.UtcNow - d.CreatedAt;
        sb.AppendLine($"- 创建时间: {d.CreatedAt:u}");
        sb.AppendLine($"- 存活时长: {elapsed.TotalSeconds:F0} 秒");
        sb.AppendLine($"- 最后活动: {d.LastActivity:u}");
        if (!string.IsNullOrEmpty(d.FinalOutput))
        {
            var preview = d.FinalOutput.Length > 500 ? d.FinalOutput[..500] + "..." : d.FinalOutput;
            sb.AppendLine($"- 最终输出预览:\n{preview}");
        }
        return Task.FromResult(new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "session_id": { "type": "string", "description": "子 agent 的会话 ID" }
        },
        "required": ["session_id"]
    }
    """)!;
}