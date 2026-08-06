using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.BasicTools
{
    /// <summary>
    /// 列出所有活跃 session 的快照。纯查询工具，不产生输出。
    /// </summary>
    [ToolMeta(Group = null, ContinueLoop = false)]
    public class ListSessionsTool : ITool
    {
        private readonly ISessionAccess? _sessionAccess;

        public ListSessionsTool() { }

        public ListSessionsTool(ISessionAccess sessionAccess) => _sessionAccess = sessionAccess;

        public string Name => "list_sessions";

        public string Description =>
            "列出所有活跃 session 的快照：sessionId、type(express/working/task)、status、占用频道名、父会话id、轮次、最后活跃时间、活跃核心。"
            + "纯查询，不产生对外输出。sessionId 可作为 send_session_message 的目标。";

        public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (_sessionAccess == null)
                return new ToolResult { Status = "failed", Error = "会话服务不可用" };

            var sessions = await _sessionAccess.ListSessionsAsync();
            var payload = sessions.Select(s => new
            {
                sessionId = s.SessionId,
                type = s.Type,
                status = s.Status,
                channel = s.Channel,
                parent = s.Parent,
                rounds = s.Rounds,
                lastActive = s.LastActive.ToString("O"),
                core = s.Core
            });
            return new ToolResult { Status = "success", Data = JsonSerializer.Serialize(payload) };
        }
    }
}
