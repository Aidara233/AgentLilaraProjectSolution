// Plugins/Plugin.SystemTools/ListSubAgentsTool.cs
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "列出活跃的子agent")]
public class ListSubAgentsTool : ITool
{
    private readonly ISubAgentAccess _subAgents;
    private readonly string _parentLoopId;

    public ListSubAgentsTool(ISubAgentAccess subAgents, string parentLoopId)
    {
        _subAgents = subAgents;
        _parentLoopId = parentLoopId;
    }

    public string Name => "list_sub_agents";
    public string Description =>
        "列出当前活跃的子 agent。默认仅返回本会话创建的子 agent（按 loopId 过滤）。" +
        "返回信息含 session_id、状态、关联的 request_id。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("only_mine", "仅返回本会话创建的子 agent（默认 true）。false=返回所有已登记子 agent", 0, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var onlyMine = resolvedInputs.Count > 0
            ? !string.Equals(resolvedInputs[0], "false", System.StringComparison.OrdinalIgnoreCase)
            : true;

        var list = _subAgents.List(onlyMine ? _parentLoopId : null);
        if (list.Count == 0)
            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = onlyMine ? "当前无活跃子 agent（本会话）" : "当前无活跃子 agent"
            });

        var sb = new StringBuilder();
        foreach (var info in list)
        {
            var status = info.IsAlive ? "运行中" : "已终止";
            var parent = string.IsNullOrEmpty(info.ParentLoopId) ? "(无)" : info.ParentLoopId;
            sb.AppendLine($"- session_id: {info.SessionId} | 状态: {status} | request_id: {info.RequestId ?? "(无)"} | 父: {parent}");
        }
        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = sb.ToString().TrimEnd()
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "only_mine": { "type": "boolean", "description": "仅返回本会话创建的子 agent（默认 true）" }
        },
        "required": []
    }
    """)!;
}