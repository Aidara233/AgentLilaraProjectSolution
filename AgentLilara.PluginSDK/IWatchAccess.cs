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

/// <summary>一条订阅的对外视图。</summary>
public sealed class WatchInfo
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Note { get; set; }
    public int MaxFires { get; set; }
    public int FiredCount { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>订阅操作的结果。</summary>
public sealed class WatchResult
{
    public bool Success { get; set; }
    public string? WatchId { get; set; }
    public string? Error { get; set; }

    public static WatchResult Ok(string watchId) => new() { Success = true, WatchId = watchId };

    public static WatchResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// 订阅访问面：让 agent 把"我待会儿发给你个邮件，你盯着点"变成一条框架长期执行的机械规则。
/// <para>只暴露**创建 / 取消 / 列举**——模型不能写图，只能给预制图填参数（D62）。</para>
/// <para>所有操作以频道为单位：订阅绑频道而不绑会话，会话死了下次命中时重建。</para>
/// <para>⚠️ 能力边界与 `preset.watch` 一致：当前只支持按发件人过滤。</para>
/// </summary>
public interface IWatchAccess
{
    WatchResult Create(int channelId, WatchRequest request);

    bool Cancel(int channelId, string watchId);

    IReadOnlyList<WatchInfo> List(int channelId);
}
