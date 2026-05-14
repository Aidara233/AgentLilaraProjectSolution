using System.Text.Json;

namespace AgentCoreProcessor.Logging;

public class SignalContext
{
    private static readonly AsyncLocal<SignalContext?> _current = new();
    private static LogWriter? _writer;
    private static int _minLevel = LogLevel.Info;

    public static SignalContext? Current => _current.Value;

    public string SignalId { get; private init; } = "";
    public string Scope { get; private init; } = "";
    public long Branch { get; private set; }
    public long? CurrentSpanId { get; private set; }

    public static void Init(LogWriter writer, int minLevel = LogLevel.Info)
    {
        _writer = writer;
        _minLevel = minLevel;
    }

    public static void SetMinLevel(int level) => _minLevel = level;

    public static string NewSignalId() => $"sig-{Guid.NewGuid():N}";

    public static SignalContext Begin(string group, string scope, string name, object? detail = null)
    {
        var ctx = new SignalContext
        {
            SignalId = NewSignalId(),
            Scope = scope,
        };
        _current.Value = ctx;

        var spanId = GenerateSpanId();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ctx.Branch = ts;
        ctx.CurrentSpanId = ts;

        var evt = new LogEvent
        {
            SignalId = ctx.SignalId,
            Scope = scope,
            Branch = ts,
            ParentId = null,
            SpanId = spanId,
            GroupName = group,
            Level = LogLevel.Info,
            Type = "open",
            Timestamp = ts,
            Name = name,
            Detail = SerializeDetail(detail)
        };
        _writer?.Enqueue(evt);
        return ctx;
    }

    public static SignalContext Continue(string signalId, long? parentSpanId, string scope, string group, string name, object? detail = null)
    {
        var ctx = new SignalContext
        {
            SignalId = signalId,
            Scope = scope,
        };
        _current.Value = ctx;

        var spanId = GenerateSpanId();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ctx.Branch = ts;
        ctx.CurrentSpanId = ts;

        var evt = new LogEvent
        {
            SignalId = signalId,
            Scope = scope,
            Branch = ts,
            ParentId = parentSpanId,
            SpanId = spanId,
            GroupName = group,
            Level = LogLevel.Info,
            Type = "open",
            Timestamp = ts,
            Name = name,
            Detail = SerializeDetail(detail)
        };
        _writer?.Enqueue(evt);
        return ctx;
    }

    public static void Restore(SignalContext? ctx) => _current.Value = ctx;

    public SpanHandle Open(string group, string name, object? detail = null)
    {
        var spanId = GenerateSpanId();
        var parentId = CurrentSpanId;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var evt = new LogEvent
        {
            SignalId = SignalId,
            Scope = Scope,
            Branch = Branch,
            ParentId = parentId,
            SpanId = spanId,
            GroupName = group,
            Level = LogLevel.Info,
            Type = "open",
            Timestamp = ts,
            Name = name,
            Detail = SerializeDetail(detail)
        };
        _writer?.Enqueue(evt);

        var prevSpanId = CurrentSpanId;
        CurrentSpanId = ts;
        return new SpanHandle(this, group, spanId, prevSpanId);
    }

    internal void CloseSpan(string group, string spanId, long? prevSpanId, object? detail)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var evt = new LogEvent
        {
            SignalId = SignalId,
            Scope = Scope,
            Branch = Branch,
            ParentId = CurrentSpanId,
            SpanId = spanId,
            GroupName = group,
            Level = LogLevel.Info,
            Type = "close",
            Timestamp = ts,
            Name = "",
            Detail = SerializeDetail(detail)
        };
        _writer?.EnqueueClose(evt);
        CurrentSpanId = prevSpanId;
    }

    public void Event(string group, string name, int level = LogLevel.Info, object? detail = null)
    {
        if (level < _minLevel) return;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var evt = new LogEvent
        {
            SignalId = SignalId,
            Scope = Scope,
            Branch = Branch,
            ParentId = CurrentSpanId,
            SpanId = null,
            GroupName = group,
            Level = level,
            Type = "event",
            Timestamp = ts,
            Name = name,
            Detail = SerializeDetail(detail)
        };
        _writer?.Enqueue(evt);
    }

    private static string GenerateSpanId() => Guid.NewGuid().ToString("N")[..16];

    private static string? SerializeDetail(object? detail)
    {
        if (detail == null) return null;
        if (detail is string s) return s;
        return JsonSerializer.Serialize(detail);
    }
}

public class SpanHandle : IDisposable
{
    public static readonly SpanHandle Noop = new();

    private readonly SignalContext? _ctx;
    private readonly string? _group;
    private readonly string? _spanId;
    private readonly long? _prevSpanId;
    private object? _closeDetail;
    private bool _disposed;

    private SpanHandle() { _disposed = true; } // Noop constructor

    internal SpanHandle(SignalContext ctx, string group, string spanId, long? prevSpanId)
    {
        _ctx = ctx;
        _group = group;
        _spanId = spanId;
        _prevSpanId = prevSpanId;
    }

    public void SetCloseDetail(object detail) => _closeDetail = detail;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ctx?.CloseSpan(_group!, _spanId!, _prevSpanId, _closeDetail);
    }
}
