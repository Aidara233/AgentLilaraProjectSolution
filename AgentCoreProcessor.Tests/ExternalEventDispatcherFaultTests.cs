using AgentCoreProcessor.Engine.Graph;
using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// T4：分发器的失败与账本边界。
/// <para>已有测试覆盖了命中/未命中/去重/失败不占键；这里补的是两个 catch 分支
/// 与账本容量上限——后者关系到长跑进程会不会无界增长。</para>
/// </summary>
public class ExternalEventDispatcherFaultTests
{
    [Fact]
    public async Task DeliverExceptionIsReportedAndDoesNotBurnTheKey()
    {
        var dispatcher = new ExternalEventDispatcher(
            _ => new[] { Runner() },
            (_, _) => throw new InvalidOperationException("投递炸了"));

        var outcome = await dispatcher.DispatchAsync(Mail("m1"));

        Assert.Equal(1, outcome.Matched);
        Assert.Equal(0, outcome.Delivered);
        Assert.Contains(outcome.Failures, f => f.Contains("投递炸了"));

        // 失败不占键：下一轮还得能重试
        Assert.False(dispatcher.HasDelivered("s1", "m1"));
    }

    [Fact]
    public async Task RetryAfterFailureSucceeds()
    {
        var attempts = 0;

        var dispatcher = new ExternalEventDispatcher(
            _ => new[] { Runner() },
            (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("第一次失败");
                }

                return Task.FromResult<string?>(null);
            });

        var @event = Mail("m1");

        var first = await dispatcher.DispatchAsync(@event);
        Assert.Equal(0, first.Delivered);

        var second = await dispatcher.DispatchAsync(@event);
        Assert.Equal(1, second.Delivered);
        Assert.True(dispatcher.HasDelivered("s1", "m1"));
    }

    [Fact]
    public async Task ThrowingDeliverForOneSubscriptionDoesNotStopTheOthers()
    {
        var dispatcher = new ExternalEventDispatcher(
            _ => new[] { Runner("s1", 42), Runner("s2", 43) },
            (channelId, _) => channelId == 42
                ? throw new InvalidOperationException("42 坏了")
                : Task.FromResult<string?>(null));

        var outcome = await dispatcher.DispatchAsync(Mail("m1"));

        Assert.Equal(2, outcome.Matched);
        Assert.Equal(1, outcome.Delivered);              // s2 照投
        Assert.Single(outcome.Failures);
        Assert.True(dispatcher.HasDelivered("s2", "m1"));
        Assert.False(dispatcher.HasDelivered("s1", "m1"));
    }

    [Fact]
    public async Task LedgerIsBounded()
    {
        var dispatcher = new ExternalEventDispatcher(
            _ => new[] { Runner() },
            (_, _) => Task.FromResult<string?>(null),
            ledgerCapacity: 2);

        await dispatcher.DispatchAsync(Mail("m1"));
        await dispatcher.DispatchAsync(Mail("m2"));
        await dispatcher.DispatchAsync(Mail("m3"));

        // 最老的被挤掉，最近的还在——账本有界，不会随长跑无限增长
        Assert.False(dispatcher.HasDelivered("s1", "m1"));
        Assert.True(dispatcher.HasDelivered("s1", "m2"));
        Assert.True(dispatcher.HasDelivered("s1", "m3"));
    }

    [Fact]
    public async Task NonPositiveCapacityFallsBackToDefault()
    {
        // 传 0 不该把账本变成"什么都记不住"——否则同一封信会被反复投递
        var dispatcher = new ExternalEventDispatcher(
            _ => new[] { Runner() },
            (_, _) => Task.FromResult<string?>(null),
            ledgerCapacity: 0);

        var @event = Mail("m1");
        await dispatcher.DispatchAsync(@event);
        var second = await dispatcher.DispatchAsync(@event);

        Assert.Equal(1, second.Duplicate);
    }

    // ── 帮手 ──

    /// <summary>一张最小图：任何外部事件都产出一个 Notify 意图。</summary>
    private static SubscriptionRunner Runner(string subscriptionId = "s1", int channelId = 42)
    {
        var graph = GraphLoader.Load("""
        {
          "classId": "test.notify",
          "kind": "static",
          "nodes": [
            { "id": "on", "type": "on.external" },
            { "id": "n", "type": "sink.notify" }
          ],
          "links": [ { "from": "on.event", "to": "n.event" } ]
        }
        """);

        var instance = new GraphInstance(graph, $"test:{subscriptionId}");

        var subscription = new GraphSubscription
        {
            Id = subscriptionId,
            ChannelId = channelId,
            ClassId = "test.notify",
            Source = "mail",
        };

        return new SubscriptionRunner(
            subscription,
            KeywordGraphRunner.ForInstance(instance, active: true));
    }

    private static InboundEvent Mail(string dedupeKey) =>
        InboundEvent.External("mail", "body", senderId: "a@b", dedupeKey: dedupeKey);
}
