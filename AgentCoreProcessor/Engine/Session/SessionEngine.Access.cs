using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Session;

internal partial class SessionEngine
{
    public async Task<List<SessionSummary>> ListSessionsAsync()
    {
        var sessions = GetActiveSessions();
        var snapBySessionId = GetChannelSessionSnapshots().ToDictionary(s => s.SessionId);
        var result = new List<SessionSummary>(sessions.Count);
        foreach (var session in sessions)
        {
            string? channelName = null;
            if (snapBySessionId.TryGetValue(session.SessionId, out var snapshot))
                channelName = (await GetChannelByIdAsync(snapshot.ChannelId))?.Name;
            var runtime = session as RuntimeSession;
            result.Add(new SessionSummary
            {
                SessionId = session.SessionId,
                Type = runtime?.Type ?? "",
                Status = session.Status.ToString(),
                Channel = channelName,
                Parent = session.ParentSessionId,
                Rounds = runtime?.RoundCountInternal ?? 0,
                LastActive = runtime?.LastActivityInternal ?? default,
                Core = runtime?.ActiveCoreConfig ?? ""
            });
        }
        return result;
    }

    public bool SendMessageAsync(string targetSessionId, string senderSessionId, string content)
    {
        var target = GetSession(targetSessionId) as RuntimeSession;
        if (target is null || !target.IsAlive) return false;
        target.InjectExternal($"[会话 {senderSessionId} 消息] {content}");
        WakeUpSession(targetSessionId);
        return true;
    }
}
