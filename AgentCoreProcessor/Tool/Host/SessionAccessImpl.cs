using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    /// <summary>
    /// ISessionAccess 实现：桥接 SessionEngine，提供全局活跃会话快照与 session 间消息。
    /// </summary>
    internal class SessionAccessImpl : ISessionAccess
    {
        private readonly ISystemContext _ctx;

        public SessionAccessImpl(ISystemContext ctx)
        {
            _ctx = ctx;
        }

        private SessionEngine? GetSessionEngine()
            => _ctx.GetActiveEnginesSnapshot().OfType<SessionEngine>().FirstOrDefault();

        public async Task<List<SessionSummary>> ListSessionsAsync()
        {
            var se = GetSessionEngine();
            if (se == null) return new List<SessionSummary>();

            var sessions = se.GetActiveSessions();
            // 一次取全频道占用快照，建 sessionId → 快照（含 ChannelId）映射
            var snapBySessionId = se.GetChannelSessionSnapshots()
                .ToDictionary(s => s.SessionId);

            var result = new List<SessionSummary>(sessions.Count);
            foreach (var s in sessions)
            {
                string? channelName = null;
                if (snapBySessionId.TryGetValue(s.SessionId, out var snap))
                {
                    var ch = await se.GetChannelByIdAsync(snap.ChannelId);
                    channelName = ch?.Name;
                }

                var rs = s as RuntimeSession;
                result.Add(new SessionSummary
                {
                    SessionId = s.SessionId,
                    Type = rs?.Type ?? "",
                    Status = s.Status.ToString(),
                    Channel = channelName,
                    Parent = s.ParentSessionId,
                    Rounds = rs?.RoundCountInternal ?? 0,
                    LastActive = rs?.LastActivityInternal ?? default,
                    Core = rs?.ActiveCoreConfig ?? ""
                });
            }
            return result;
        }

        public bool SendMessageAsync(string targetSessionId, string senderSessionId, string content)
        {
            var se = GetSessionEngine();
            if (se == null) return false;

            var target = se.GetSession(targetSessionId) as RuntimeSession;
            if (target == null || !target.IsAlive) return false;

            target.InjectExternal($"[会话 {senderSessionId} 消息] {content}");
            se.WakeUpSession(targetSessionId);
            return true;
        }
    }
}
