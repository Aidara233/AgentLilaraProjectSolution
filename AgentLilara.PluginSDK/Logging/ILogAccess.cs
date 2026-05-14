namespace AgentLilara.PluginSDK.Logging;

public interface ILogAccess : ISignalLogger
{
    List<LogEventInfo> GetBySignal(string signalId);
    List<LogEventInfo> GetByScope(string scope, long? since = null, int limit = 200);
    List<LogEventInfo> GetRecent(int limit = 200, string? group = null, int? minLevel = null);
    List<OpenSpanSummary> GetOpenSpans();
    List<LogEventInfo> GetSignalList(int limit = 50);
    List<TokenUsageInfo> GetTokenUsage(long? since = null, string? model = null, string? callerTag = null);
    IDisposable Subscribe(Action<IReadOnlyList<LogEventInfo>> callback);
    void Cleanup(int? retainDays = null);
}
