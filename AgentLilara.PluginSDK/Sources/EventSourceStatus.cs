namespace AgentLilara.PluginSDK.Sources;

/// <summary>子信源连接状态。</summary>
public enum EventSourceState
{
    Stopped,
    Starting,
    Running,
    Error,
}

/// <summary>一个子信源的健康快照。</summary>
public sealed record EventSourceStatus
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public EventSourceState State { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public long Received { get; init; }
    public DateTimeOffset? LastEventAt { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastErrorAt { get; init; }
}
