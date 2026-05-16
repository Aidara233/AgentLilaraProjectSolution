using System.Text.Json;

namespace AgentCoreProcessor.Logging;

public class SignalContext : IDisposable
{
    private static readonly AsyncLocal<SignalContext?> _current = new();
    private static LogWriter? _writer;
    private static int _minLevel = LogLevel.Info;

    public static SignalContext? Current => _current.Value;

    public string SignalId { get; private init; } = "";
    public string Scope { get; private init; } = "";
    public long Branch { get; private set; }
    public string? CurrentSpanId { get; private set; }

    private string? _rootSpanId;
    private string? _rootGroup;
    private string? _rootName;
    private bool _disposed;

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
        ctx.CurrentSpanId = spanId;
        ctx._rootSpanId = spanId;
        ctx._rootGroup = group;
        ctx._rootName = name;

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
            Detail = SerializeDetail(detail),
            IsSignalOrigin = true
        };
        _writer?.Enqueue(evt);
        return ctx;
    }

    public static SignalContext Continue(string signalId, string? parentSpanId, string scope, string group, string name, object? detail = null)
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
        ctx.CurrentSpanId = spanId;
        ctx._rootSpanId = spanId;
        ctx._rootGroup = group;
        ctx._rootName = name;

        var evt = new LogEvent
        {
            SignalId = signalId,
            Scope = scope,
            Branch = ts,
            ParentId = null,           // new root span within this scope
            SpanId = spanId,
            CauseSpanId = parentSpanId, // cross-scope causation
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
        var parentSpanId = CurrentSpanId;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var evt = new LogEvent
        {
            SignalId = SignalId,
            Scope = Scope,
            Branch = Branch,
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

        var prevSpanId = CurrentSpanId;
        CurrentSpanId = spanId;
        return new SpanHandle(this, group, spanId, prevSpanId, name);
    }

    internal void CloseSpan(string group, string spanId, string? prevSpanId, string name, object? detail)
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
            Name = name,
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

    public void Close(object? detail = null)
    {
        if (_disposed || _rootSpanId == null) return;
        _disposed = true;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var evt = new LogEvent
        {
            SignalId = SignalId,
            Scope = Scope,
            Branch = Branch,
            ParentId = _rootSpanId,
            SpanId = _rootSpanId,
            GroupName = _rootGroup!,
            Level = LogLevel.Info,
            Type = "close",
            Timestamp = ts,
            Name = _rootName ?? "",
            Detail = SerializeDetail(detail)
        };
        _writer?.EnqueueClose(evt);
    }

    public void Dispose() => Close();

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
    private readonly string? _prevSpanId;
    private readonly string _name;
    private object? _closeDetail;
    private bool _disposed;

    private SpanHandle() { _disposed = true; _name = ""; } // Noop constructor

    internal SpanHandle(SignalContext ctx, string group, string spanId, string? prevSpanId, string name)
    {
        _ctx = ctx;
        _group = group;
        _spanId = spanId;
        _prevSpanId = prevSpanId;
        _name = name;
    }

    public void SetCloseDetail(object detail) => _closeDetail = detail;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ctx?.CloseSpan(_group!, _spanId!, _prevSpanId, _name, _closeDetail);
    }
}
