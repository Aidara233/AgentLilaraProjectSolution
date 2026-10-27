namespace AgentCoreProcessor.Engine;

/// <summary>一个频道的唯一占用状态。调用方在资源池协调锁内访问。</summary>
internal sealed class ChannelOccupancyState
{
    public string ActiveSessionId { get; private set; } = "";
    public bool HasFullClaim => !string.IsNullOrEmpty(ActiveSessionId);
    // Compatibility with the current coordinator; encapsulating mutation is a later step.
    public List<string> ListenSessionIds { get; } = new();

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
