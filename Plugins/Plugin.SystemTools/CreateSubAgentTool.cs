// Plugins/Plugin.SystemTools/CreateSubAgentTool.cs
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "创建子agent执行任务")]
public class CreateSubAgentTool : ITool
{
    private readonly ISubAgentAccess _subAgents;

    public CreateSubAgentTool(ISubAgentAccess subAgents)
    {
        _subAgents = subAgents;
    }

    public string Name => "create_sub_agent";
    public string Description => "创建并启动子agent来执行指定任务。可关联委托ID以自动回写结果。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("instruction", "子agent执行指令", 0),
        new("delegation_id", "关联的委托ID（可选）", 1, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var instruction = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var delegationId = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;

        if (string.IsNullOrWhiteSpace(instruction))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "instruction 不能为空" });

        var info = string.IsNullOrWhiteSpace(delegationId)
            ? _subAgents.Create(instruction)
            : _subAgents.Create(instruction, delegationId);

        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"子agent已创建。session_id: {info.SessionId}"
        });
    }

    public JsonNode GetInputSchema()
    {
        return JsonNode.Parse("""
        {
            "type": "object",
            "properties": {
                "instruction": { "type": "string", "description": "子agent执行指令" },
                "delegation_id": { "type": "string", "description": "关联的委托ID（可选，用于自动回写结果）" }
            },
            "required": ["instruction"]
        }
        """)!;
    }
}
