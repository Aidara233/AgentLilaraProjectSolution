using AgentLilara.PluginSDK.Logging;

namespace AgentCoreProcessor.Logging;

public class LogAccessImpl : ILogAccess
{
    private readonly LogWriter _writer;
    private readonly LogQuery _query;
    private readonly OpenSpanTracker _spanTracker;
    private readonly LogDatabase _db;

    public LogAccessImpl(LogWriter writer, LogQuery query, OpenSpanTracker spanTracker, LogDatabase db)
    {
        _writer = writer;
        _query = query;
        _spanTracker = spanTracker;
        _db = db;
    }

    // ISignalLogger — delegate to SignalContext.Current
    public IDisposable Open(string group, string name, object? detail = null)
        => SignalContext.Current?.Open(group, name, detail) ?? (IDisposable)SpanHandle.Noop;

    public void Event(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Info, detail);

    public void Debug(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Debug, detail);

    public void Warn(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Warn, detail);

    public void Error(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Error, detail);

    // ILogAccess query methods — delegate to LogQuery, convert types
    public List<LogEventInfo> GetBySignal(string signalId)
        => _query.GetBySignal(signalId).Select(ToInfo).ToList();

    public List<LogEventInfo> GetByScope(string scope, long? since = null, int limit = 200)
        => _query.GetByScope(scope, since, limit).Select(ToInfo).ToList();

    public List<LogEventInfo> GetRecent(int limit = 200, string? group = null, int? minLevel = null)
        => _query.GetRecent(limit, group, minLevel).Select(ToInfo).ToList();

    public List<OpenSpanSummary> GetOpenSpans()
        => _spanTracker.GetCurrentlyRunning().Select(s => new OpenSpanSummary
        {
            SpanId = s.SpanId,
            SignalId = s.SignalId,
            Scope = s.Scope,
            GroupName = s.GroupName,
            Name = s.Name,
            StartedAt = s.StartedAt
        }).ToList();

    public List<LogEventInfo> GetSignalList(int limit = 50)
        => _query.GetSignalList(limit).Select(ToInfo).ToList();

    public List<TokenUsageInfo> GetTokenUsage(long? since = null, string? model = null, string? callerTag = null)
        => _query.GetTokenUsage(since, model, callerTag).Select(t => new TokenUsageInfo
        {
            Timestamp = t.Timestamp,
            Model = t.Model,
            CallerTag = t.CallerTag,
            TokensIn = t.TokensIn,
            TokensOut = t.TokensOut,
            CachedIn = t.CachedIn,
            ElapsedMs = t.ElapsedMs,
            Success = t.Success
        }).ToList();

    public IDisposable Subscribe(Action<IReadOnlyList<LogEventInfo>> callback)
        => _writer.Subscribe(batch => callback(batch.Select(ToInfo).ToList()));

    public void Cleanup(int? retainDays = null)
        => _db.Cleanup(retainDays ?? 7, 90);

    private static LogEventInfo ToInfo(LogEvent e) => new()
    {
        Id = e.Id,
        SignalId = e.SignalId,
        Scope = e.Scope,
        Branch = e.Branch,
        ParentId = e.ParentId,
        SpanId = e.SpanId,
        GroupName = e.GroupName,
        Level = e.Level,
        Type = e.Type,
        Timestamp = e.Timestamp,
        Name = e.Name,
        Detail = e.Detail
    };
}
