namespace AgentCoreProcessor.Logging;

public static class Signal
{
    public static SignalContext Begin(string group, string scope, string name, object? detail = null)
        => SignalContext.Begin(group, scope, name, detail);

    public static SignalContext Continue(string signalId, long? parentSpanId, string scope, string group, string name, object? detail = null)
        => SignalContext.Continue(signalId, parentSpanId, scope, group, name, detail);

    public static string NewId() => SignalContext.NewSignalId();

    public static SpanHandle Open(string group, string name, object? detail = null)
    {
        var ctx = SignalContext.Current;
        if (ctx == null) return SpanHandle.Noop;
        return ctx.Open(group, name, detail);
    }

    public static void Event(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Info, detail);

    public static void Debug(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Debug, detail);

    public static void Warn(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Warn, detail);

    public static void Error(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Error, detail);
}
