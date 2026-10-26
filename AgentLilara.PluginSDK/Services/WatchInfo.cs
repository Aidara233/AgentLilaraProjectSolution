namespace AgentLilara.PluginSDK.Services;

/// <summary>一条订阅的对外视图。</summary>
public sealed class WatchInfo
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Note { get; set; }
    public int MaxFires { get; set; }
    public int FiredCount { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
