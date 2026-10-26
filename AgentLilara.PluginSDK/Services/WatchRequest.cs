namespace AgentLilara.PluginSDK.Services;

/// <summary>创建一个"盯着某件事"的订阅请求。</summary>
public sealed class WatchRequest
{
    /// <summary>信源类别（如 `mail`）。必填。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>只认这个发件人（可选；留空表示不限发件人）。</summary>
    public string? From { get; set; }

    /// <summary>为什么在等。会随命中内容一起交给会话——跨会话重建时它才知道来龙去脉。</summary>
    public string? Note { get; set; }

    /// <summary>有效分钟数；0 表示不过期（不推荐，会积累僵尸订阅）。</summary>
    public int TtlMinutes { get; set; }

    /// <summary>最多触发几次。默认 1（一次性）。</summary>
    public int MaxFires { get; set; } = 1;
}
