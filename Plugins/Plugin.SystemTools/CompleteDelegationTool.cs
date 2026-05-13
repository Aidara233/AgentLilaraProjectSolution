// Plugins/Plugin.SystemTools/CompleteDelegationTool.cs
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "标记委托任务已完成并回传结果")]
public class CompleteDelegationTool : ITool
{
    private readonly IDelegationAccess _delegations;

    public CompleteDelegationTool(IDelegationAccess delegations)
    {
        _delegations = delegations;
    }

    public string Name => "complete_delegation";
    public string Description => "标记委托任务已完成，将结果回传给频道循环。用于系统循环自行处理委托后报告结果。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("delegation_id", "委托ID", 0),
        new("result", "执行结果", 1),
        new("failed", "是否失败（true/false，默认false）", 2)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var delegationId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var result = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;
        var failed = resolvedInputs.Count > 2 && resolvedInputs[2]?.ToLower() == "true";

        if (string.IsNullOrWhiteSpace(delegationId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "delegation_id 不能为空" });
        if (string.IsNullOrWhiteSpace(result))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "result 不能为空" });

        if (failed)
            _delegations.MarkFailed(delegationId, result);
        else
            _delegations.MarkCompleted(delegationId, result);

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"委托 {delegationId} 已标记为{(failed ? "失败" : "完成")}，结果已回传频道循环。"
        });
    }

    public JsonNode GetInputSchema()
    {
        return JsonNode.Parse("""
        {
            "type": "object",
            "properties": {
                "delegation_id": { "type": "string", "description": "委托ID" },
                "result": { "type": "string", "description": "执行结果" },
                "failed": { "type": "string", "description": "是否失败（true/false，默认false）" }
            },
            "required": ["delegation_id", "result"]
        }
        """)!;
    }
}
