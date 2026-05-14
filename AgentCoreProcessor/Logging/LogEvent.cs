namespace AgentCoreProcessor.Logging;

public class LogEvent
{
    public long Id { get; set; }
    public string SignalId { get; set; } = "";
    public string Scope { get; set; } = "";
    public long Branch { get; set; }
    public long? ParentId { get; set; }
    public string? SpanId { get; set; }
    public string GroupName { get; set; } = "";
    public int Level { get; set; } = 1;
    public string Type { get; set; } = "event";
    public long Timestamp { get; set; }
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
}

public static class LogLevel
{
    public const int Debug = 0;
    public const int Info = 1;
    public const int Warn = 2;
    public const int Error = 3;
}

public static class LogGroup
{
    public const string Engine = "Engine";
    public const string Model = "Model";
    public const string Tool = "Tool";
    public const string Memory = "Memory";
    public const string Adapter = "Adapter";
    public const string Plugin = "Plugin";
}
