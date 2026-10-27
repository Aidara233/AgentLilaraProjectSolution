using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;

namespace AgentCoreProcessor.Engine.Graph;

/// <summary>一次外部事件分发的账目。</summary>
internal sealed record DispatchOutcome(
    int Matched,
    int Delivered,
    int Duplicate,
    IReadOnlyList<string> Failures)
{
    public static readonly DispatchOutcome Empty =
        new(0, 0, 0, Array.Empty<string>());
}

/// <summary>
/// 外部事件 → 订阅 → 判定 → 投递。
/// <para>只依赖两个委托（候选订阅 / 投递），不碰 <c>ISystemContext</c>，因此可以直接用假实现单测。</para>
/// <para>语义要点：</para>
/// <list type="bullet">
/// <item>粗筛按信源（<see cref="GraphSubscription.Source"/>），精判归图；框架不复制一份条件语言。</item>
/// <item>图没产出 <see cref="IntentKind.Notify"/> 就是"没命中"——静默，不打扰会话，也不花 token。</item>
/// <item>投递键**成功之后才占**：失败必须留给重试，不能被记账挡掉。</item>
/// <item>同一订阅对同一事件只投一次（信源重投、游标回退都不会造成重复唤醒）。</item>
/// </list>
/// </summary>
internal sealed class ExternalEventDispatcher
{
    /// <summary>投递账本的默认容量：超出后丢一半最老的。防止长跑进程无界增长。</summary>
    public const int DefaultLedgerCapacity = 4096;

    private readonly Func<string, IReadOnlyList<SubscriptionRunner>> _candidates;
    private readonly Func<int, string, Task<string?>> _deliver;
    private readonly Action<string, DateTimeOffset>? _onFired;
    private readonly int _ledgerCapacity;

    private readonly HashSet<string> _deliveredKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _deliveredOrder = new();

    /// <param name="candidates">按信源取候选订阅（粗筛）。</param>
    /// <param name="deliver">投递到频道（参数：channelId、文本）；成功返回 null，失败返回原因。</param>
    /// <param name="onFired">投递成功后的记账回调（累加订阅的触发次数）。</param>
    public ExternalEventDispatcher(
        Func<string, IReadOnlyList<SubscriptionRunner>> candidates,
        Func<int, string, Task<string?>> deliver,
        Action<string, DateTimeOffset>? onFired = null,
        int ledgerCapacity = DefaultLedgerCapacity)
    {
        _candidates = candidates;
        _deliver = deliver;
        _onFired = onFired;
        _ledgerCapacity = ledgerCapacity > 0 ? ledgerCapacity : DefaultLedgerCapacity;
    }

    public async Task<DispatchOutcome> DispatchAsync(InboundEvent @event)
    {
        var candidates = _candidates(@event.SourceId);
        if (candidates.Count == 0)
        {
            return DispatchOutcome.Empty;
        }

        var traceId = $"external:{@event.DedupeKey}";
        var delivered = 0;
        var duplicate = 0;
        var failures = new List<string>();

        foreach (var candidate in candidates)
        {
            var subscription = candidate.Subscription;
            var deliveryKey = $"{subscription.Id}:{@event.DedupeKey}";

            if (_deliveredKeys.Contains(deliveryKey))
            {
                duplicate++;
                continue;
            }

            IReadOnlyList<ActionIntent> intents;
            try
            {
                intents = await candidate.Runner.EvaluateAsync(@event, traceId);
            }
            catch (Exception ex)
            {
                failures.Add($"{subscription.Id}:evaluate-failed:{ex.Message}");
                continue;
            }

            if (!intents.Any(intent => intent.Kind == IntentKind.Notify))
            {
                // 没命中：静默。这正是"框架替模型盯着"的成本优势所在。
                continue;
            }

            var text = SubscriptionNotice.Render(subscription, @event);

            string? failure;
            try
            {
                failure = await _deliver(subscription.ChannelId, text);
            }
            catch (Exception ex)
            {
                failure = $"deliver-threw:{ex.Message}";
            }

            if (failure is not null)
            {
                failures.Add($"{subscription.Id}:{failure}");
                continue;
            }

            Remember(deliveryKey);
            _onFired?.Invoke(subscription.Id, @event.ReceivedAt);
            delivered++;
        }

        return new DispatchOutcome(candidates.Count, delivered, duplicate, failures);
    }

    /// <summary>投递账本里是否已有这一条（测试与诊断用）。</summary>
    public bool HasDelivered(string subscriptionId, string dedupeKey) =>
        _deliveredKeys.Contains($"{subscriptionId}:{dedupeKey}");

    private void Remember(string deliveryKey)
    {
        if (!_deliveredKeys.Add(deliveryKey))
        {
            return;
        }

        _deliveredOrder.Enqueue(deliveryKey);

        while (_deliveredOrder.Count > _ledgerCapacity)
        {
            _deliveredKeys.Remove(_deliveredOrder.Dequeue());
        }
    }
}
