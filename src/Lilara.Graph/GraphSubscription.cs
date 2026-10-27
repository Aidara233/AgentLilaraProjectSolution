using System.Text.Json.Serialization;

namespace AgentCoreProcessor.Engine.Graph;

/// <summary>
/// 一条**运行时订阅**：某个频道在等某个信源上的某类事件，命中就唤醒它的会话。
/// <para>⚠️ 与 <see cref="GraphMount"/> 的区别：挂载是**人写的静态配置**（可版本化、随文件重载），
/// 订阅是**运行时创建的期待**（带期限、带次数上限、随触发作废）。两者共用同一套图运行时，
/// 但存在两个文件、两条重载路径，互不覆盖。</para>
/// <para>⚠️ 宿主是**频道**而不是会话：会话会死，频道不会。命中时若会话已不在，
/// 由骨架新建一个再注入——所以 <see cref="Note"/> 必须自带"为什么在等"，
/// 否则新会话收到一封邮件会完全不知道来龙去脉。</para>
/// </summary>
internal sealed class GraphSubscription
{
    /// <summary>订阅标识（进程内唯一，写入实例键）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>宿主频道。投递目标由它在命中时解析。</summary>
    public int ChannelId { get; set; }

    /// <summary>用哪张图做判定（通常是 `preset.watch`）。</summary>
    public string ClassId { get; set; } = string.Empty;

    /// <summary>信源标识（`mail` / `webhook`…）。粗筛键——框架靠它找到候选，图再做精判。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>图参数（`read.mount` 读的就是它），如 `from` / `subject`。</summary>
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>为什么在等（模型/用户原话的浓缩）。注入时一并带上，供重建的会话理解上下文。</summary>
    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>到期时间。<c>null</c> = 不过期（不推荐，会积累僵尸订阅）。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>最多触发几次。默认 1（"等我那封邮件"是一次性的）。0 或负数 = 不设上限。</summary>
    public int MaxFires { get; set; } = 1;

    public int FiredCount { get; set; }

    public DateTimeOffset? LastFiredAt { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>图实例键。与其他实例分开前缀，避免和挂载点撞车。</summary>
    [JsonIgnore]
    public string InstanceKey => $"sub:{Id}";

    /// <summary>是否已过期（惰性判定：命中检查与列表时看，不靠定时器）。</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } at && now >= at;

    /// <summary>次数是否已用尽。</summary>
    public bool IsExhausted => MaxFires > 0 && FiredCount >= MaxFires;

    /// <summary>还该不该被激活。</summary>
    public bool IsLive(DateTimeOffset now) => Enabled && !IsExpired(now) && !IsExhausted;

    /// <summary>转成图运行时的挂载参数视图。</summary>
    public IReadOnlyDictionary<string, string> AsMountParameters() =>
        new Dictionary<string, string>(Params, StringComparer.OrdinalIgnoreCase);
}

/// <summary>`Storage/Graphs/subscriptions.json` 的形状。</summary>
internal sealed class SubscriptionDocument
{
    public int Version { get; set; } = 1;

    public List<GraphSubscription> Subscriptions { get; set; } = new();
}

/// <summary>一条订阅 + 它对应的图实例。</summary>
internal sealed record SubscriptionRunner(GraphSubscription Subscription, KeywordGraphRunner Runner);
