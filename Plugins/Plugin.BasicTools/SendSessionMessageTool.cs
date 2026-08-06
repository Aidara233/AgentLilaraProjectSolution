using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.BasicTools
{
    /// <summary>
    /// 向指定 session 发送消息（即时注入目标上下文并唤醒）。纯操作工具，不产生对外输出。
    /// </summary>
    [ToolMeta(Group = null, ContinueLoop = false)]
    public class SendSessionMessageTool : ITool
    {
        private readonly ISessionAccess? _sessionAccess;
        private readonly string _senderSessionId;

        public SendSessionMessageTool() => _senderSessionId = "";

        public SendSessionMessageTool(ISessionAccess sessionAccess, string senderSessionId)
        {
            _sessionAccess = sessionAccess;
            _senderSessionId = senderSessionId;
        }

        public string Name => "send_session_message";

        public string Description =>
            "向指定 session 发送一条消息：立即注入目标会话上下文并唤醒它，目标会话下一轮会看到 [会话 你 消息] 并处理。"
            + "注意：会打断目标会话当前任务；单发不自动回复，如需结果请让目标完成后回报（如子 agent 用 report_to_parent）。"
            + "session_id 来自 list_sessions。";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("session_id", "目标 session id（来自 list_sessions）", 0),
            new("content", "消息内容", 1)
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (_sessionAccess == null)
                return new ToolResult { Status = "failed", Error = "会话服务不可用" };

            var target = resolvedInputs.Count > 0 ? resolvedInputs[0]?.Trim() : null;
            var content = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;
            if (string.IsNullOrWhiteSpace(target))
                return new ToolResult { Status = "failed", Error = "session_id 不能为空" };
            if (string.IsNullOrWhiteSpace(content))
                return new ToolResult { Status = "failed", Error = "content 不能为空" };

            var ok = _sessionAccess.SendMessageAsync(target, _senderSessionId, content);
            return ok
                ? new ToolResult { Status = "success", Data = $"消息已发送到会话 {target}" }
                : new ToolResult { Status = "failed", Error = $"目标会话 {target} 不存在或未活跃" };
        }
    }
}
