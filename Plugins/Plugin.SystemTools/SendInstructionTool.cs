// Plugins/Plugin.SystemTools/SendInstructionTool.cs
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "向运行中的子agent追加指令")]
public class SendInstructionTool : ITool
{
    private readonly ISubAgentAccess _subAgents;

    public SendInstructionTool(ISubAgentAccess subAgents)
    {
        _subAgents = subAgents;
    }

    public string Name => "send_instruction";
    public string Description => "向运行中的子agent追加一条指令。子agent会在当前轮次结束后处理新指令。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("session_id", "子agent的会话ID", 0),
        new("instruction", "追加的指令内容", 1)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var sessionId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var instruction = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;

        if (string.IsNullOrWhiteSpace(sessionId))
            return new ToolResult { Status = "failed", Error = "session_id 不能为空" };
        if (string.IsNullOrWhiteSpace(instruction))
            return new ToolResult { Status = "failed", Error = "instruction 不能为空" };

        var info = _subAgents.Get(sessionId);
        if (info == null)
            return new ToolResult { Status = "failed", Error = $"子agent {sessionId} 不存在" };
        if (!info.IsAlive)
            return new ToolResult { Status = "failed", Error = $"子agent {sessionId} 已终止" };

        var ok = await _subAgents.SendInstructionAsync(sessionId, instruction);
        return new ToolResult
        {
            Status = ok ? "success" : "failed",
            Data = ok ? $"指令已追加到 {sessionId}" : $"追加失败: {sessionId}"
        };
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "session_id": { "type": "string", "description": "子agent的会话ID" },
            "instruction": { "type": "string", "description": "追加的指令内容" }
        },
        "required": ["session_id", "instruction"]
    }
    """)!;
}
