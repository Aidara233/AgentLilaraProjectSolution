using System.Collections.Concurrent;
using AgentCoreProcessor.Logging;
using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.PluginSDK.Sources;

namespace AgentCoreProcessor.Engine.Source;

/// <summary>
/// 信源汇集器：**与 <see cref="AgentCoreProcessor.Adapter.AdapterManager"/> 平级**的一层入站产生者。
/// <para>职责刻意只有四件事：登记子信源、启停、汇总健康、把子信源的事件转给统一入口。
/// 它不理解邮件、也不理解订阅——那是子信源和订阅分发的事。</para>
/// <para>⚠️ 与适配器管理器的分工：适配器是**双向对话通道**（有频道、能发、走消息管线）；
/// 子信源是**单向事件流**（无频道、不能发、只走订阅分发）。两者只在 <see cref="InboundEvent"/> 汇合，
/// 不共用注册表、不共用状态面板、不互相回退。</para>
/// <para>⚠️ 子信源契约来自 <see cref="AgentLilara.PluginSDK.Sources"/>：插件（net8）与宿主（net10）
/// 必须共用同一个接口类型，所以事件形状用 SDK 的 <see cref="SourceEvent"/>，
/// 在这里翻译成宿主内部的 <see cref="InboundEvent"/>。翻译只做一次，语义只有一套。</para>
/// </summary>
internal sealed class SourceManager
{
    private readonly ConcurrentDictionary<string, IEventSource> _sources = new(StringComparer.Ordinal);

    /// <summary>
    /// 启停失败的留痕：子信源自己抛异常时，它的 <c>GetStatus()</c> 往往还停在旧状态
    /// （启动失败根本没走到更新那一步）。健康汇总必须以**汇集器观察到的结果**为准，否则面板上看着是 Running。
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Error, DateTimeOffset At)> _failures =
        new(StringComparer.Ordinal);

    /// <summary>汇总出口：所有子信源的事件都从这里出去（由 MasterEngine 在启动时注入）。</summary>
    public Func<InboundEvent, Task>? OnEvent { get; set; }

    /// <summary>当前登记的子信源数（诊断用）。</summary>
    public int Count => _sources.Count;

    /// <summary>登记一个子信源。重名则拒绝（调用方决定是否先 Unregister）。</summary>
    public bool Register(IEventSource source)
    {
        if (!_sources.TryAdd(source.Id, source))
        {
            Signal.Warn(LogGroup.Engine, "source.register.duplicate", new { sourceId = source.Id });
            return false;
        }

        source.OnEvent += evt => ForwardAsync(source, evt);

        Signal.Event(LogGroup.Engine, "source.registered", new { sourceId = source.Id, kind = source.Kind });
        return true;
    }

    public bool Unregister(string id)
    {
        if (!_sources.TryRemove(id, out _))
        {
            return false;
        }

        Signal.Event(LogGroup.Engine, "source.unregistered", new { sourceId = id });
        return true;
    }

    public IEventSource? Get(string id) => _sources.GetValueOrDefault(id);

    public IReadOnlyList<IEventSource> All => _sources.Values.ToList();

    public IReadOnlyList<EventSourceStatus> GetAllStatuses() =>
        _sources.Values.Select(WithObservedFailure).ToList();

    /// <summary>启动全部子信源。单个失败只记该子信源，不阻断其余（失败隔离）。</summary>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        foreach (var source in _sources.Values)
        {
            try
            {
                await source.StartAsync(ct);
                _failures.TryRemove(source.Id, out _);
            }
            catch (Exception ex)
            {
                _failures[source.Id] = (ex.Message, DateTimeOffset.UtcNow);
                Signal.Warn(LogGroup.Engine, "source.start.failed",
                    new { sourceId = source.Id, error = ex.Message });
            }
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var source in _sources.Values)
        {
            try
            {
                await source.StopAsync();
            }
            catch (Exception ex)
            {
                _failures[source.Id] = (ex.Message, DateTimeOffset.UtcNow);
                Signal.Warn(LogGroup.Engine, "source.stop.failed",
                    new { sourceId = source.Id, error = ex.Message });
            }
        }
    }

    /// <summary>
    /// 子信源 → 统一入口。**吞掉下游异常**：一条事件处理失败不该让子信源崩溃，
    /// 否则一次订阅投递故障会连带停掉整个信源。
    /// </summary>
    private async Task ForwardAsync(IEventSource source, SourceEvent @event)
    {
        if (@event is null)
        {
            return;
        }

        var handler = OnEvent;
        if (handler is null)
        {
            Signal.Debug(LogGroup.Engine, "source.event.dropped",
                new { sourceId = source.Id, reason = "no-handler" });
            return;
        }

        try
        {
            await handler(ToInbound(@event));
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "source.event.forward_failed",
                new { sourceId = source.Id, @event.DedupeKey, error = ex.Message });
        }
    }

    /// <summary>
    /// 插件事件形状 → 宿主事件形状。
    /// <para>外部事件**没有频道**：<c>ChannelKey</c> 留空，因此它不会被按频道挂载的图看见，
    /// 投递目标由订阅记录决定。去重键原样带过，订阅分发靠它防重复唤醒。</para>
    /// </summary>
    internal static InboundEvent ToInbound(SourceEvent @event)
    {
        var dedupe = string.IsNullOrWhiteSpace(@event.DedupeKey)
            ? $"{@event.SourceId}:{@event.SenderId}:{@event.Text.GetHashCode():x}"
            : @event.DedupeKey;

        // 归一成大小写不敏感的字典：插件可能用默认比较器构造，而图上的 event.ext 键名是约定，
        // 不该因为插件写成 "Subject" 还是 "subject" 就查不到。
        Dictionary<string, string>? ext = null;
        if (@event.Ext is { Count: > 0 })
        {
            ext = new Dictionary<string, string>(@event.Ext, StringComparer.OrdinalIgnoreCase);
        }

        return InboundEvent.External(
            sourceId: string.IsNullOrWhiteSpace(@event.SourceId) ? "unknown" : @event.SourceId,
            text: @event.Text ?? string.Empty,
            senderId: @event.SenderId,
            ext: ext,
            dedupeKey: dedupe,
            at: @event.ReceivedAt);
    }

    /// <summary>子信源自报的状态 + 汇集器观察到的启停失败，后者的优先级更高。</summary>
    private EventSourceStatus WithObservedFailure(IEventSource source)
    {
        var status = SafeStatus(source);

        return _failures.TryGetValue(source.Id, out var failure)
            ? status with
            {
                State = EventSourceState.Error,
                LastError = failure.Error,
                LastErrorAt = failure.At,
            }
            : status;
    }

    private static EventSourceStatus SafeStatus(IEventSource source)
    {
        try
        {
            return source.GetStatus();
        }
        catch (Exception ex)
        {
            return new EventSourceStatus
            {
                Id = source.Id,
                Kind = source.Kind,
                State = EventSourceState.Error,
                LastError = ex.Message,
                LastErrorAt = DateTimeOffset.UtcNow,
            };
        }
    }
}
