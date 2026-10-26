using AgentLilara.GraphRuntime.Contracts;

namespace AgentCoreProcessor.Engine.Source;

/// <summary>子信源连接状态。</summary>
internal enum EventSourceState
{
    Stopped,
    Starting,
    Running,
    Error,
}

/// <summary>一个子信源的健康快照。</summary>
internal sealed record EventSourceStatus
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
/// 子信源：**单向事件流**。邮件、webhook、定时、文件变化都实现它。
/// <para>⚠️ 与 <see cref="AgentCoreProcessor.Adapter.IAdapter"/> 的根本差别：</para>
/// <list type="bullet">
/// <item>没有频道 —— 事件不带对话归属，投递目标由订阅记录决定；</item>
/// <item>不能发送 —— 没有 <c>SendMessageAsync</c>，"回一封信"是另一条链路；</item>
/// <item>可能是拉取式的 —— 因此有游标与 <c>StartAsync</c> 生命周期，而不是长连接回调。</item>
/// </list>
/// <para>子信源自己管自己的游标、去重与重连；汇集器不理解业务，只负责登记、启停、汇总与转发。</para>
/// </summary>
internal interface IEventSource
{
    /// <summary>子信源实例标识（配置里的唯一键）。</summary>
    string Id { get; }

    /// <summary>事件的信源类别，会成为 <see cref="InboundEvent.SourceId"/>（如 `mail`）。</summary>
    string Kind { get; }

    /// <summary>产出一条事件。汇集器负责把它交给统一入站入口。</summary>
    event Func<InboundEvent, Task>? OnEvent;

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync();

    EventSourceStatus GetStatus();
}
