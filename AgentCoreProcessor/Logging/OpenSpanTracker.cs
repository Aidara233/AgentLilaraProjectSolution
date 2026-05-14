using System.Collections.Concurrent;

namespace AgentCoreProcessor.Logging;

public class OpenSpanInfo
{
    public string SpanId { get; init; } = "";
    public string SignalId { get; init; } = "";
    public string Scope { get; init; } = "";
    public string GroupName { get; init; } = "";
    public string Name { get; init; } = "";
    public long StartedAt { get; init; }
}

public class OpenSpanTracker
{
    private readonly ConcurrentDictionary<string, OpenSpanInfo> _openSpans = new();

    public void TrackOpen(LogEvent evt)
    {
        if (evt.SpanId == null) return;
        _openSpans[evt.SpanId] = new OpenSpanInfo
        {
            SpanId = evt.SpanId,
            SignalId = evt.SignalId,
            Scope = evt.Scope,
            GroupName = evt.GroupName,
            Name = evt.Name,
            StartedAt = evt.Timestamp
        };
    }

    public void TrackClose(string spanId)
    {
        _openSpans.TryRemove(spanId, out _);
    }

    public IReadOnlyCollection<OpenSpanInfo> GetCurrentlyRunning() => _openSpans.Values.ToList();
}
