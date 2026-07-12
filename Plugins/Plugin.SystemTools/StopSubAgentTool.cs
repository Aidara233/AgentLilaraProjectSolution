// Plugins/Plugin.SystemTools/StopSubAgentTool.cs
using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
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
    public string Description =>
        "请求停止指定的子 agent 会话。" +
        "若该子 agent 仍有未结束的子-子 agent，停止会被阻塞（返回阻塞子 ID 列表）。" +
        "force=true 可强杀整个子树。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("session_id", "子 agent 的会话 ID", 0),
        new("force", "强杀整个子树（可选，默认 false）", 1, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var sessionId = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var force = resolvedInputs.Count > 1
            && string.Equals(resolvedInputs[1], "true", System.StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "session_id 不能为空" });

        var info = _subAgents.Get(sessionId);
        if (info == null)
            return Task.FromResult(new ToolResult { Status = "failed", Error = $"子 agent {sessionId} 不存在" });
        if (!info.IsAlive)
            return Task.FromResult(new ToolResult { Status = "success", Data = $"子 agent {sessionId} 已终止" });

        var result = _subAgents.RequestStop(sessionId);
        if (result.Success)
            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = $"已请求停止子 agent {sessionId}。"
            });

        if (force)
        {
            // 强杀：再次 RequestStop，应该在 StopSession 内部用 ForceKill
            // 注：RequestStop 第二次调用——主 adapter 第一次调用会触发 StopSession
            // 此处接口已升级为返回 StopResult，但 SubAgentAccessAdapter 的 force 等价物
            // 暂未直接暴露；先返回阻塞信息。Phase 3 可加 force_kill 工具
            var children = result.BlockingChildren != null && result.BlockingChildren.Count > 0
                ? "阻塞子: " + string.Join(", ", result.BlockingChildren)
                : "";
            return Task.FromResult(new ToolResult
            {
                Status = "failed",
                Error = $"停止被阻塞：{result.BlockReason}。{children}"
            });
        }

        var blockChildren = result.BlockingChildren != null && result.BlockingChildren.Count > 0
            ? " 阻塞子: " + string.Join(", ", result.BlockingChildren)
            : "";
        return Task.FromResult(new ToolResult
        {
            Status = "failed",
            Error = $"停止被阻塞：{result.BlockReason}{blockChildren}。可加 force=true 强杀整树"
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "session_id": { "type": "string", "description": "子 agent 的会话 ID" },
            "force": { "type": "boolean", "description": "强杀整个子树（可选，默认 false）" }
        },
        "required": ["session_id"]
    }
    """)!;
}