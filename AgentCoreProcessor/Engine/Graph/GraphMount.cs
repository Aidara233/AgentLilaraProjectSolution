namespace AgentCoreProcessor.Engine.Graph;

/// <summary>一条挂载记录：哪个作用域用哪张图、哪份参数。</summary>
internal sealed class GraphMount
{
    /// <summary>作用域：global / channel / session。</summary>
    public string Scope { get; set; } = "channel";

    /// <summary>作用域键：channel 是内部频道 id；session 是 RuntimeSession.SessionId；global 为空。</summary>
    public string? ScopeKey { get; set; }

    /// <summary>图类 id，对应 Storage/Graphs/{classId}.json。</summary>
    public string ClassId { get; set; } = string.Empty;

    /// <summary>同一作用域内区分同名类的实例名；不填则只能挂一次该 classId。</summary>
    public string? InstanceName { get; set; }

    /// <summary>同一作用域内的执行顺序（小的先跑）。顺序不影响语义，只影响日志与意图提交次序。</summary>
    public int Order { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>挂载参数，例如 preset.router 的 sessionType。</summary>
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>实例键：挂载点唯一标识。classId 可被多个挂载点共用，实例绝不共用。</summary>
    public string InstanceKey =>
        $"{ClassId}@{Scope}#{ScopeKey ?? string.Empty}#{InstanceName ?? string.Empty}";
}
