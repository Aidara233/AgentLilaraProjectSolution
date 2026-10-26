namespace AgentLilara.PluginSDK.Sources;

/// <summary>子信源连接状态。</summary>
public enum EventSourceState
{
    Stopped,
    Starting,
    Running,
    Error,
}

/// <summary>一个子信源的健康快照。</summary>
public sealed record EventSourceStatus
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public EventSourceState State { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public long Received { get; init; }
    public DateTimeOffset? LastEventAt { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastErrorAt { get; init; }
}

/// <summary>
/// 一条外部信源事件。**信源与宿主之间的唯一形状**。
/// <para>为什么不是宿主的 <c>InboundEvent</c>：那是 net10 程序集里的类型，插件（net8）够不着。
/// 这一层刻意只有"信源能产生的东西"，不带任何频道/会话概念——外部事件本来就没有频道，
/// 投递目标由订阅记录决定。</para>
/// </summary>
public sealed class SourceEvent
{
    /// <summary>信源标识（`mail` / `webhook`…）。粗筛键，框架按它找候选订阅。</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>正文（邮件即正文纯文本）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>发件人（可选）。</summary>
    public string? SenderId { get; set; }

    /// <summary>信源特有的结构化字段（如 subject / uid / folder）。缺失的键在图上表现为 unknown。</summary>
    public Dictionary<string, string> Ext { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>去重键。同一封信重复投递必须给同一个值（如 uidvalidity:uid）。</summary>
    public string DedupeKey { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 子信源：**单向事件流**。邮件、webhook、定时、文件变化都实现它。
/// <para>⚠️ 与 <c>IAdapter</c> 的根本差别：</para>
/// <list type="bullet">
/// <item>没有频道 —— 事件不带对话归属，投递目标由订阅记录决定；</item>
/// <item>不能发送 —— 没有 <c>SendMessageAsync</c>，"回一封信"是另一条链路；</item>
/// <item>可能是拉取式的 —— 因此有游标与 <c>StartAsync</c> 生命周期，而不是长连接回调。</item>
/// </list>
/// <para>子信源自己管自己的游标、去重与重连；汇集器不理解业务，只负责登记、启停、汇总与转发。</para>
/// <para>⚠️ 这个接口是**插件可见契约**：实现它并在 <c>StartAsync</c> 里跑自己的循环，
/// 宿主侧的汇集器会在启动时把它拉起来。</para>
/// </summary>
public interface IEventSource
{
    /// <summary>子信源实例标识（配置里的唯一键）。</summary>
    string Id { get; }

    /// <summary>事件的信源类别，会成为 <see cref="SourceEvent.SourceId"/>（如 `mail`）。</summary>
    string Kind { get; }

    /// <summary>产出一条事件。汇集器负责把它交给统一入站入口。</summary>
    event Func<SourceEvent, Task>? OnEvent;

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync();

    EventSourceStatus GetStatus();
}
