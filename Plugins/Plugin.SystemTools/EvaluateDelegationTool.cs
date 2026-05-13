// Plugins/Plugin.SystemTools/EvaluateDelegationTool.cs
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "评估频道循环提交的委托任务")]
public class EvaluateDelegationTool : ITool
{
    private readonly IDelegationAccess _delegations;

    public EvaluateDelegationTool(IDelegationAccess delegations)
    {
        _delegations = delegations;
    }

    public string Name => "evaluate_delegation";
    public string Description => "评估频道循环提交的委托任务，决定接受、排队或拒绝。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("delegation_id", "委托ID", 0),
        new("verdict", "评估结果: accept/queue/reject", 1),
        new("reason", "评估理由", 2)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var delegationId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var verdict = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;
        var reason = resolvedInputs.Count > 2 ? resolvedInputs[2] : null;

        if (string.IsNullOrWhiteSpace(delegationId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "delegation_id 不能为空" });
        if (string.IsNullOrWhiteSpace(verdict))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "verdict 不能为空" });
        if (verdict != "accept" && verdict != "queue" && verdict != "reject")
            return Task.FromResult(new ToolResult { Status = "failed", Error = "verdict 必须是 accept/queue/reject" });
        if (string.IsNullOrWhiteSpace(reason))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "reason 不能为空" });

        var ok = _delegations.ResolveEvaluation(delegationId, verdict, reason);
        if (!ok)
            return Task.FromResult(new ToolResult { Status = "failed", Error = $"委托 {delegationId} 不存在或已被评估" });

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"委托 {delegationId} 已评估为 {verdict}。"
        });
    }

    public JsonNode GetInputSchema()
    {
        return JsonNode.Parse("""
        {
            "type": "object",
            "properties": {
                "delegation_id": { "type": "string", "description": "委托ID" },
                "verdict": { "type": "string", "enum": ["accept", "queue", "reject"], "description": "评估结果" },
                "reason": { "type": "string", "description": "评估理由" }
            },
            "required": ["delegation_id", "verdict", "reason"]
        }
        """)!;
    }
}
