// Plugins/Plugin.DelegationTools/CancelDelegationTool.cs
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.DelegationTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "取消委托任务")]
public class CancelDelegationTool : ITool
{
    private readonly IDelegationAccess _delegations;

    public CancelDelegationTool(IDelegationAccess delegations)
    {
        _delegations = delegations;
    }

    public string Name => "cancel_delegation";
    public string Description => "取消一个委托任务，将其从委托列表中移除。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("delegation_id", "要取消的委托ID", 0)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var delegationId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;

        if (string.IsNullOrWhiteSpace(delegationId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "delegation_id 不能为空" });

        var ok = _delegations.Cancel(delegationId);
        if (!ok)
            return Task.FromResult(new ToolResult { Status = "failed", Error = $"委托 {delegationId} 不存在" });

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"委托 {delegationId} 已取消。"
        });
    }

    public JsonNode GetInputSchema()
    {
        return JsonNode.Parse("""
        {
            "type": "object",
            "properties": {
                "delegation_id": { "type": "string", "description": "要取消的委托ID" }
            },
            "required": ["delegation_id"]
        }
        """)!;
    }
}
