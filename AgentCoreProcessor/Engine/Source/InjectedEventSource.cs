using AgentLilara.PluginSDK.Sources;

namespace AgentCoreProcessor.Engine.Source;

/// <summary>
/// 进程内注入子信源：把一条 <see cref="SourceEvent"/> 直接交给汇集器。
/// <para>用途是**让链路可跑通、可测**：真实信源（IMAP、webhook）需要外部依赖与网络，
/// 而"汇集器 → 订阅 → 会话"这段逻辑不该被它们挡住。</para>
/// <para>也供调试与手工触发使用；它不持有游标、不做去重（去重由订阅的投递账本负责）。</para>
/// </summary>
internal sealed class InjectedEventSource : IEventSource
{
    private long _received;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastEventAt;
    private EventSourceState _state = EventSourceState.Stopped;

    public InjectedEventSource(string id = "manual", string kind = "injected")
    {
        Id = id;
        Kind = kind;
    }

    public string Id { get; }

    public string Kind { get; }

    public event Func<SourceEvent, Task>? OnEvent;

    public Task StartAsync(CancellationToken ct = default)
    {
        _state = EventSourceState.Running;
        _startedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _state = EventSourceState.Stopped;
        return Task.CompletedTask;
    }

    /// <summary>注入一条事件。返回 false 表示汇集器尚未接上出口（没人在听）。</summary>
    public async Task<bool> PublishAsync(SourceEvent @event)
    {
        var handler = OnEvent;
        if (handler is null)
        {
            return false;
        }

        _received++;
        _lastEventAt = DateTimeOffset.UtcNow;
        await handler(@event);
        return true;
    }

    public EventSourceStatus GetStatus() => new()
    {
        Id = Id,
        Kind = Kind,
        State = _state,
        StartedAt = _startedAt,
        Received = _received,
        LastEventAt = _lastEventAt,
    };
}
