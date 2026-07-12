// Plugins/Plugin.SystemTools/WaitSubTool.cs
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

/// <summary>
/// 阻塞等待指定子 agent 完成（或超时）。
/// 父 session 调用本工具后当前轮次会挂起直到子 agent 完成。
/// 注意：长等待会消耗本 session 工具执行时间；推荐 timeout_ms 在 1-30 秒内。
/// 若需要异步等待，不调用本工具即可——子 agent 完成时会自动注入 [子会话 N 已完成] 通知。
/// </summary>
[ToolMeta(ContinueLoop = true, CapabilitySummary = "等待子agent完成")]
public class WaitSubTool : ITool
{
    private readonly ISubAgentAccess _subAgents;

    public WaitSubTool(ISubAgentAccess subAgents) => _subAgents = subAgents;

    public string Name => "wait_sub";
    public string Description =>
        "阻塞等待指定子 agent 完成。同步返回 true=已完成 / false=超时未完成。" +
        "不调用本工具时子 agent 完成也会通知你；本工具用于必须串行依赖子产出的场景。" +
        "建议 timeout_ms 不超过 30000（30 秒），避免长时间占用会话。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("session_id", "子 agent 的会话 ID", 0),
        new("timeout_ms", "等待超时毫秒（默认 10000，最大 60000）", 1, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(65);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var sessionId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        if (string.IsNullOrWhiteSpace(sessionId))
            return new ToolResult { Status = "failed", Error = "session_id 不能为空" };

        var info = _subAgents.Get(sessionId);
        if (info == null)
            return new ToolResult { Status = "failed", Error = $"子 agent {sessionId} 不存在" };
        if (!info.IsAlive)
            return new ToolResult
            {
                Status = "success",
                Data = $"子 agent {sessionId} 已完成（无需等待）"
            };

        int timeoutMs = 10000;
        if (resolvedInputs.Count > 1 && int.TryParse(resolvedInputs[1], out var t) && t > 0)
            timeoutMs = System.Math.Min(t, 60000);

        var done = await _subAgents.WaitAsync(sessionId, timeoutMs);
        return new ToolResult
        {
            Status = "success",
            Data = done
                ? $"子 agent {sessionId} 已完成。"
                : $"等待超时（{timeoutMs} ms）：子 agent {sessionId} 仍在运行。"
        };
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "session_id": { "type": "string", "description": "子 agent 的会话 ID" },
            "timeout_ms": { "type": "integer", "description": "等待超时毫秒（默认 10000，最大 60000）" }
        },
        "required": ["session_id"]
    }
    """)!;
}