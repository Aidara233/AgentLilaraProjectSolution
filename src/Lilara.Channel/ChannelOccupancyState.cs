namespace AgentCoreProcessor.Engine;

/// <summary>一个频道的唯一占用状态。调用方在资源池协调锁内访问。</summary>
internal sealed class ChannelOccupancyState
{
    public string ActiveSessionId { get; private set; } = "";
    public bool HasFullClaim => !string.IsNullOrEmpty(ActiveSessionId);
    private readonly List<string> _listeners = new();
    public IReadOnlyList<string> ListenSessionIds => Array.AsReadOnly(_listeners.ToArray());
    public bool HasListeners => _listeners.Count != 0;

    public bool Listen(string sessionId)
    {
        if (_listeners.Contains(sessionId)) return false;
        _listeners.Add(sessionId);
        return true;
    }

    public bool Unlisten(string sessionId) => _listeners.RemoveAll(id => id == sessionId) != 0;

    public bool Claim(string sessionId)
    {
        if (ActiveSessionId == sessionId) return true;
        if (HasFullClaim) return false;
        ActiveSessionId = sessionId;
        return true;
    }

    public void TakeOver(string sessionId) => ActiveSessionId = sessionId;

    public bool Release(string sessionId)
    {
        if (ActiveSessionId != sessionId) return false;
        ActiveSessionId = "";
        return true;
    }
}
