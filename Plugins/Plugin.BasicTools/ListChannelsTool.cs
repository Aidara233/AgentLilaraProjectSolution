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
    /// 列出程序内所有频道。纯查询工具，不产生输出。
    /// </summary>
    [ToolMeta(Group = null, ContinueLoop = false)]
    public class ListChannelsTool : ITool
    {
        private readonly IChannelAccess? _channelAccess;

        public ListChannelsTool() { }

        public ListChannelsTool(IChannelAccess channelAccess) => _channelAccess = channelAccess;

        public string Name => "list_channels";

        public string Description =>
            "列出程序内所有频道：id、name（作为 search_chat_history 的 channel 参数）、消息总数、最后活跃时间、"
            + "占用会话 id 与类型（空 = 空闲）。纯查询，不产生对外输出。";

        public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (_channelAccess == null)
                return new ToolResult { Status = "failed", Error = "频道服务不可用" };

            var channels = await _channelAccess.GetAllChannelsAsync();
            var payload = channels.Select(c => new
            {
                id = c.Id,
                name = c.Name,
                sessionId = c.SessionId,
                sessionType = c.SessionType,
                messages = c.MessageCount,
                lastActive = c.LastActive?.ToString("O")
            });
            return new ToolResult { Status = "success", Data = JsonSerializer.Serialize(payload) };
        }
    }
}
