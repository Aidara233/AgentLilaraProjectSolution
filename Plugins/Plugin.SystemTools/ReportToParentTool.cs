// Plugins/Plugin.SystemTools/ReportToParentTool.cs
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

/// <summary>
/// 子 agent 专用工具：向父 session 回报进度或完成。仅在 sub-agent-runtime 组件中可见。
/// 调用方为子 agent 自身（callerLoopId = "session:xxx"），SubAgentAccessAdapter 据此反查自己的 sessionId，
/// 通过 RuntimeSession.InjectExternal 直接注入父 session 的下一轮上下文。
/// </summary>
[ToolMeta(ContinueLoop = true)]
public class ReportToParentTool : ITool
{
    private readonly ISubAgentAccess _subAgents;
    private readonly string _callerLoopId;

    public ReportToParentTool(ISubAgentAccess subAgents, string callerLoopId)
    {
        _subAgents = subAgents;
        _callerLoopId = callerLoopId;
    }

    public string Name => "report_to_parent";
    public string Description =>
        "向父会话回报进度或完成结果。" +
        "report 后父 session 在下一轮上下文中将收到 [子会话 N 回报] 系统消息。" +
        "任务完成时请用 state=done 回传最终结果（content 应含产出的摘要文本）。" +
        "失败时用 state=failed 说明原因。" +
        "纯进度更新可多次调用 state=progress，每次都会注入父上下文。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("content", "回报内容文本（完成时为最终结果摘要，进度时为当前进度描述）", 0),
        new("state", "done=完成 / progress=进行中 / failed=失败（可选，默认 progress）", 1, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var content = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var stateStr = resolvedInputs.Count > 1 ? resolvedInputs[1] : "progress";

        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "content 不能为空" });

        var state = (stateStr ?? "").ToLowerInvariant() switch
        {
            "done" or "complete" or "completed" => ReportState.Done,
            "failed" or "fail" or "error" => ReportState.Failed,
            _ => ReportState.Progress
        };

        var ok = _subAgents.ReportToParent(_callerLoopId, content, state);
        return Task.FromResult(new ToolResult
        {
            Status = ok ? "success" : "failed",
            Data = ok
                ? $"已向父会话回报 ({state})。父 will 在下一轮上下文看到本消息。"
                : $"回报失败：调用方 ({_callerLoopId}) 未在子 agent 表中"
        });
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "content": { "type": "string", "description": "回报内容（最终结果/进度/失败原因）" },
            "state": { "type": "string", "enum": ["progress", "done", "failed"], "description": "进度/完成/失败" }
        },
        "required": ["content"]
    }
    """)!;
}