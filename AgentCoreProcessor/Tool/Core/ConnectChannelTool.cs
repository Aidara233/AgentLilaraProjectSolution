using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    /// <summary>
    /// 连接到频道（Full 占用）：抢占频道、停其 express，订阅消息（normal+mention），可发言。
    /// 实际的 claim/订阅/状态更新由 RuntimeSession 在工具副作用段执行（工具本身够不到 session 私有状态）。
    /// </summary>
    [ToolMeta(ContinueLoop = true, EngineTypes = new[] { "session" })]
    internal class ConnectChannelTool : ITool
    {
        public string Name => "connect_channel";
        public string Description => "连接到指定频道并占用它（暂停该频道的自动回复），之后 speak 会发到此频道。"
            + "会自动断开当前已连接的频道（同时只能连一个）。连接后会载入该频道的历史消息。";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("channel_id", "要连接的频道 ID", 0)
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var channelId = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
            return Task.FromResult(new ToolResult { Status = "success", Data = channelId });
        }
    }

    /// <summary>
    /// 旁听频道（Listen，非独占）：订阅消息但不占用、不打断该频道 express，<b>不能发言</b>。
    /// 适合只看不说的观察场景。
    /// </summary>
    [ToolMeta(ContinueLoop = true, EngineTypes = new[] { "session" })]
    internal class ListenChannelTool : ITool
    {
        public string Name => "listen_channel";
        public string Description => "旁听指定频道：能看到该频道的消息，但不占用频道、不打断其自动回复，"
            + "也不能在该频道发言（只看不说）。会自动断开当前已连接的频道（同时只能连一个）。";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("channel_id", "要旁听的频道 ID", 0)
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var channelId = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
            return Task.FromResult(new ToolResult { Status = "success", Data = channelId });
        }
    }

    /// <summary>
    /// 断开当前频道连接：释放占用（恢复该频道 express）、退订消息。断开后无法发言直到再次连接。
    /// </summary>
    [ToolMeta(ContinueLoop = true, EngineTypes = new[] { "session" })]
    internal class DisconnectChannelTool : ITool
    {
        public string Name => "disconnect_channel";
        public string Description => "断开当前连接的频道，释放占用并恢复该频道的自动回复。断开后无法发言，直到再次连接。";

        public IReadOnlyList<ToolParameter> Parameters => [];

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
            => Task.FromResult(new ToolResult { Status = "success" });
    }
}
