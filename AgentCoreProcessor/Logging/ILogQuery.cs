namespace AgentCoreProcessor.Logging;

public interface ILogQuery
{
    List<LogEvent> GetBySignal(string signalId);
    List<LogEvent> GetByScope(string scope, long? since = null, int limit = 200);
    List<LogEvent> GetRecent(int limit = 200, string? group = null, int? minLevel = null);
    List<LogEvent> GetOpenSpans();
    List<LogEvent> GetSignalList(int limit = 50);
    List<TokenUsageRecord> GetTokenUsage(long? since = null, string? model = null, string? callerTag = null);
}

public class TokenUsageRecord
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
