using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.WebUI.Services;

internal class LogTraceService
{
    private readonly ILogQuery _query;
    private readonly LogWriter _writer;

    public LogTraceService(ILogQuery query, LogWriter writer)
    {
        _query = query;
        _writer = writer;
    }

    public IDisposable Subscribe(Action<TraceRow> onNewRow)
    {
        return _writer.Subscribe(batch =>
        {
            foreach (var evt in batch)
            {
                var row = new TraceRow
                {
                    Id = evt.Id,
                    SignalId = evt.SignalId,
                    Scope = evt.Scope,
                    ParentId = evt.ParentId,
                    SpanId = evt.SpanId,
                    CauseSpanId = evt.CauseSpanId,
                    Type = evt.Type,
                    Level = evt.Level,
                    Timestamp = evt.Timestamp,
                    Name = evt.Name,
                    Detail = evt.Detail,
                    GroupName = evt.GroupName,
                    IsSignalOrigin = evt.IsSignalOrigin
                };
                onNewRow(row);
            }
        });
    }

    public List<SignalSummary> GetSignalList(int limit = 50)
    {
        var roots = _query.GetSignalList(limit);
        return roots.Select(e => new SignalSummary
        {
            SignalId = e.SignalId,
            Scope = e.Scope,
            Name = e.Name,
            Timestamp = e.Timestamp
        }).ToList();
    }

    public TraceViewModel GetTrace(string signalId, TraceFilter? filter = null)
    {
        var events = _query.GetBySignal(signalId);
        var vm = BuildViewModel(events, filter);
        // 单信号轨迹一次性取全量，游标只为与页面模型保持一致。
        vm.HasOlder = false;
        if (events.Count > 0)
        {
            var oldest = events.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).First();
            vm.CursorTimestamp = oldest.Timestamp;
            vm.CursorId = oldest.Id;
        }
        return vm;
    }

    public TraceViewModel GetRecentTrace(int limit = 500, TraceFilter? filter = null)
    {
        // 多取一条判断是否还有更早事件（含过滤场景）。
        var events = _query.GetRecent(limit + 1);
        var hasOlder = events.Count > limit;
        var page = hasOlder ? events.Take(limit).ToList() : events;
        var vm = BuildViewModel(page, filter);
        vm.HasOlder = hasOlder;
        if (page.Count > 0)
        {
            var oldest = page.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).First();
            vm.CursorTimestamp = oldest.Timestamp;
            vm.CursorId = oldest.Id;
        }
        return vm;
    }

    public TraceViewModel GetTraceBefore(string signalId, long beforeTimestamp, int limit = 200)
        => GetTraceBefore(signalId, beforeTimestamp, long.MaxValue, limit);

    public TraceViewModel GetTraceBefore(string signalId, long beforeTimestamp, long beforeId, int limit = 200)
    {
        var events = _query.GetSignalBefore(signalId, beforeTimestamp, beforeId, limit + 1);
        var hasOlder = events.Count > limit;
        var page = hasOlder ? events.Take(limit).ToList() : events;
        var vm = BuildViewModel(page);
        vm.HasOlder = hasOlder;
        if (page.Count > 0)
        {
            var oldest = page.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).First();
            vm.CursorTimestamp = oldest.Timestamp;
            vm.CursorId = oldest.Id;
        }
        return vm;
    }

    public TraceViewModel GetRecentBefore(long beforeTimestamp, int limit = 200, TraceFilter? filter = null)
        => GetRecentBefore(beforeTimestamp, long.MaxValue, limit, filter);

    public TraceViewModel GetRecentBefore(long beforeTimestamp, long beforeId, int limit = 200, TraceFilter? filter = null)
    {
        // 多取一条用于判断是否还有更早事件；过滤在内存中进行，游标使用原始 SQL 结果。
        var raw = _query.GetRecentBefore(beforeTimestamp, beforeId, limit + 1);
        var hasOlder = raw.Count > limit;
        var page = hasOlder ? raw.Take(limit).ToList() : raw;
        var vm = BuildViewModel(page, filter);
        vm.HasOlder = hasOlder;
        // 过滤只影响展示；游标基于未过滤的原始页，保证“加载更早”持续推进且不漏事件。
        if (page.Count > 0)
        {
            var oldest = page.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).First();
            vm.CursorTimestamp = oldest.Timestamp;
            vm.CursorId = oldest.Id;
        }
        return vm;
    }

    private TraceViewModel BuildViewModel(List<LogEvent> events, TraceFilter? filter = null)
    {
        var scopes = events.Select(e => e.Scope).Distinct().ToList();
        var filtered = filter != null ? ApplyFilter(events, filter) : events;
        var rows = filtered
            .OrderBy(e => e.Timestamp).ThenBy(e => e.Id)
            .Select(evt => new TraceRow
            {
                Id = evt.Id,
                SignalId = evt.SignalId,
                Scope = evt.Scope,
                ParentId = evt.ParentId,
                SpanId = evt.SpanId,
                CauseSpanId = evt.CauseSpanId,
                Type = evt.Type,
                Level = evt.Level,
                Timestamp = evt.Timestamp,
                Name = evt.Name,
                Detail = evt.Detail,
                GroupName = evt.GroupName,
                IsSignalOrigin = evt.IsSignalOrigin
            })
            .ToList();

        return new TraceViewModel { Scopes = scopes, Rows = rows };
    }

    private List<LogEvent> ApplyFilter(List<LogEvent> events, TraceFilter filter)
    {
        IEnumerable<LogEvent> result = events;

        if (filter.MinLevel > 0)
            result = result.Where(e => e.Level >= filter.MinLevel);

        if (filter.VisibleScopes != null)
            result = result.Where(e => filter.VisibleScopes.Contains(e.Scope));

        if (filter.OpenSpansOnly)
        {
            var closedSpans = events.Where(e => e.Type == "close").Select(e => e.SpanId).ToHashSet();
            var stuckOpens = events.Where(e => e.Type == "open" && !closedSpans.Contains(e.SpanId)).ToList();
            var openBySpanId = events.Where(e => e.Type == "open" && e.SpanId != null)
                .ToDictionary(e => e.SpanId!, e => e.Id);
            var keepIds = new HashSet<long>();
            foreach (var open in stuckOpens)
            {
                keepIds.Add(open.Id);
                var current = open;
                while (current.ParentId != null)
                {
                    if (openBySpanId.TryGetValue(current.ParentId, out var parentRowId))
                    {
                        keepIds.Add(parentRowId);
                        current = events.FirstOrDefault(e => e.Id == parentRowId);
                        if (current == null) break;
                    }
                    else break;
                }
            }
            result = result.Where(e => keepIds.Contains(e.Id));
        }

        return result.ToList();
    }
}

internal class SignalSummary
{
    public string SignalId { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Name { get; set; } = "";
    public long Timestamp { get; set; }
}

internal class TraceViewModel
{
    public List<string> Scopes { get; set; } = new();
    public List<TraceRow> Rows { get; set; } = new();
    public bool HasOlder { get; set; }
    public long? CursorTimestamp { get; set; }
    public long? CursorId { get; set; }
}

internal class TraceRow
{
    public long Id { get; set; }
    public string SignalId { get; set; } = "";
    public string Scope { get; set; } = "";
    public string? ParentId { get; set; }
    public string? SpanId { get; set; }
    public string? CauseSpanId { get; set; }
    public string Type { get; set; } = "";
    public int Level { get; set; }
    public long Timestamp { get; set; }
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("isSignalOrigin")]
    public bool IsSignalOrigin { get; set; }
    public string GroupName { get; set; } = "";
}

internal class TraceFilter
{
    public int MinLevel { get; set; } = 0;
    public HashSet<string>? VisibleScopes { get; set; }
    public bool OpenSpansOnly { get; set; }
}
