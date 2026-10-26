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

/// <summary>一个信源能提供什么：给工具描述与错误提示用，让模型不用猜。</summary>
public sealed class SourceCapability
{
    /// <summary>信源类别（`mail` / `webhook`…），也就是订阅时要填的 `source`。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>该信源会提供的载荷字段。为空表示它没有声明（不参与键校验）。</summary>
    public IReadOnlyList<string> Fields { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 订阅访问面：让 agent 把"我待会儿发给你个邮件，你盯着点"变成一条框架长期执行的机械规则。
/// <para>只暴露**创建 / 取消 / 列举**——模型不能写图，只能给预制图填参数（D62）。</para>
/// <para>所有操作以频道为单位：订阅绑频道而不绑会话，会话死了下次命中时重建。</para>
/// </summary>
public interface IWatchAccess
{
    WatchResult Create(int channelId, WatchRequest request);

    bool Cancel(int channelId, string watchId);

    IReadOnlyList<WatchInfo> List(int channelId);

    /// <summary>
    /// 当前有哪些信源可用、各自支持哪些字段。
    /// <para>工具描述与错误提示都读它——否则模型只能猜 `source` 该填什么，
    /// 猜错的表现是"订阅成功但永远不命中"。</para>
    /// </summary>
    IReadOnlyList<SourceCapability> DescribeSources();
}
