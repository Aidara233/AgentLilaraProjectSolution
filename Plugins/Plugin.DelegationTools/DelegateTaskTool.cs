// Plugins/Plugin.DelegationTools/DelegateTaskTool.cs
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.DelegationTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "将任务委托给系统循环处理")]
public class DelegateTaskTool : ITool
{
    private readonly IDelegationAccess _delegations;
    private readonly string _loopId;

    public DelegateTaskTool(IDelegationAccess delegations, string loopId)
    {
        _delegations = delegations;
        _loopId = loopId;
    }

    public string Name => "delegate_task";
    public string Description => "将任务委托给系统循环处理。系统循环会评估后决定接受、排队或拒绝。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("description", "任务描述", 0),
        new("context", "补充上下文（可选）", 1, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(35);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var description = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var context = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;

        if (string.IsNullOrWhiteSpace(description))
            return new ToolResult { Status = "failed", Error = "description 不能为空" };

        if (!int.TryParse(_loopId, out var channelId))
            return new ToolResult { Status = "failed", Error = "无法解析频道ID" };

        var result = await _delegations.SubmitAndWaitAsync(
            description, context, channelId, personId: 0, TimeSpan.FromSeconds(30));

        if (result.TimedOut)
            return new ToolResult
            {
                Status = "success",
                Data = "委托已提交，但评估超时。系统循环可能繁忙，稍后会处理。"
            };

        return new ToolResult
        {
            Status = "success",
            Data = $"委托已提交。评估结果: {result.Verdict}（{result.Reason}）"
        };
    }

    public JsonNode GetInputSchema()
    {
        return JsonNode.Parse("""
        {
            "type": "object",
            "properties": {
                "description": { "type": "string", "description": "任务描述" },
                "context": { "type": "string", "description": "补充上下文（可选）" }
            },
            "required": ["description"]
        }
        """)!;
    }
}
