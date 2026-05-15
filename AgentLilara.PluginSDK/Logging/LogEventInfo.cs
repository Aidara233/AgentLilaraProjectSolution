namespace AgentLilara.PluginSDK.Logging;

public class LogEventInfo
{
    public long Id { get; set; }
    public string SignalId { get; set; } = "";
    public string Scope { get; set; } = "";
    public long Branch { get; set; }
    public string? ParentId { get; set; }
    public string? SpanId { get; set; }
    public string GroupName { get; set; } = "";
    public int Level { get; set; }
    public string Type { get; set; } = "";
    public long Timestamp { get; set; }
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
}

public class OpenSpanSummary
{
    public string SpanId { get; set; } = "";
    public string SignalId { get; set; } = "";
    public string Scope { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string Name { get; set; } = "";
    public long StartedAt { get; set; }
}

public class TokenUsageInfo
{
    public long Timestamp { get; set; }
    public string Model { get; set; } = "";
    public string? CallerTag { get; set; }
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public int CachedIn { get; set; }
    public int ElapsedMs { get; set; }
    public bool Success { get; set; }
}
