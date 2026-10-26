using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Source;
using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.PluginSDK.Sources;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// P3：信源汇集器（与适配器管理器平级）+ 子信源契约 + 进程内注入源。
/// <para>验的是汇集器的四条底线：重名拒绝、启停覆盖全部、单个子信源失败不牵连别人、
/// 下游异常不外溢到子信源。最后一条尤其重要——一次投递故障不该让信源自己崩掉。</para>
/// </summary>
public class EventSourceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-sources-" + Guid.NewGuid().ToString("N"));

    public EventSourceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Graphs"));
    }

    // ── 登记 ──

    [Fact]
    public void DuplicateRegistrationIsRejected()
    {
        var manager = new SourceManager();

        Assert.True(manager.Register(new InjectedEventSource("dup")));
        Assert.False(manager.Register(new InjectedEventSource("dup")));
        Assert.Equal(1, manager.Count);
    }

    [Fact]
    public void UnregisterRemovesSource()
    {
        var manager = new SourceManager();
        manager.Register(new InjectedEventSource("gone"));

        Assert.True(manager.Unregister("gone"));
        Assert.False(manager.Unregister("gone"));
        Assert.Null(manager.Get("gone"));
    }

    // ── 启停与失败隔离 ──

    [Fact]
    public async Task StartAllReachesEverySource()
    {
        var manager = new SourceManager();
        var a = new FakeSource("a");
        var b = new FakeSource("b");
        manager.Register(a);
        manager.Register(b);

        await manager.StartAllAsync();

        Assert.Equal(1, a.StartCount);
        Assert.Equal(1, b.StartCount);

        await manager.StopAllAsync();
        Assert.Equal(1, a.StopCount);
        Assert.Equal(1, b.StopCount);
    }

    [Fact]
    public async Task OneFailingSourceDoesNotBlockTheRest()
    {
        var manager = new SourceManager();
        var broken = new FakeSource("broken") { ThrowOnStart = true };
        var healthy = new FakeSource("healthy");
        manager.Register(broken);
        manager.Register(healthy);

        await manager.StartAllAsync(); // 不应抛出

        Assert.Equal(1, healthy.StartCount);
        Assert.Equal(EventSourceState.Error, manager.GetAllStatuses().Single(s => s.Id == "broken").State);
    }

    [Fact]
    public async Task ThrowingHandlerDoesNotEscapeToTheSource()
    {
        var manager = new SourceManager
        {
            // 模拟下游（订阅分发）抛异常
            OnEvent = _ => throw new InvalidOperationException("boom"),
        };

        var source = new InjectedEventSource();
        manager.Register(source);

        var published = await source.PublishAsync(Mail("boss@corp", "x"));

        Assert.True(published); // 汇集器吞掉了异常，子信源不受影响
        Assert.Equal(1, source.GetStatus().Received);
    }

    // ── 状态汇总 ──

    [Fact]
    public async Task StatusIsAggregatedPerSource()
    {
        var manager = new SourceManager();
        var source = new InjectedEventSource("manual", "injected");
        manager.Register(source);
        await manager.StartAllAsync();

        var status = Assert.Single(manager.GetAllStatuses());
        Assert.Equal("manual", status.Id);
        Assert.Equal("injected", status.Kind);
        Assert.Equal(EventSourceState.Running, status.State);
    }

    [Fact]
    public void ThrowingGetStatusIsReportedAsError()
    {
        var manager = new SourceManager();
        manager.Register(new FakeSource("bad-status") { ThrowOnStatus = true });

        var status = Assert.Single(manager.GetAllStatuses());
        Assert.Equal(EventSourceState.Error, status.State);
        Assert.Contains("boom", status.LastError);
    }

    // ── 汇集器 → 订阅 → 投递 ──

    [Fact]
    public async Task InjectedEventReachesMatchedSubscription()
    {
        var graphs = GraphManager.Open(_root, active: true);

        var watch = new GraphSubscription
        {
            ChannelId = 42,
            ClassId = "preset.watch",
            Source = "mail",
            Note = "用户说会发邮件过来",
            MaxFires = 5,
        };
        watch.Params["from"] = "boss@corp";

        Assert.Null(graphs.AddSubscription(watch));

        var delivered = new List<(int ChannelId, string Text)>();
        var dispatcher = new ExternalEventDispatcher(
            graphs.SubscriptionsForSource,
            (channelId, text) =>
            {
                delivered.Add((channelId, text));
                return Task.FromResult<string?>(null);
            },
            graphs.RecordSubscriptionFired);

        var sources = new SourceManager { OnEvent = dispatcher.DispatchAsync };
        var injected = new InjectedEventSource();
        sources.Register(injected);
        await sources.StartAllAsync();

        await injected.PublishAsync(Mail("boss@corp", "明早九点发版"));

        var one = Assert.Single(delivered);
        Assert.Equal(42, one.ChannelId);
        Assert.Contains("明早九点发版", one.Text);
        Assert.Contains("会发邮件过来", one.Text);

        // 不匹配的不会唤醒
        await injected.PublishAsync(Mail("spam@corp", "广告"));
        Assert.Single(delivered);
    }

    // ── 契约翻译（插件形状 → 宿主形状）──

    [Fact]
    public void SourceEventBecomesExternalEventWithoutChannel()
    {
        var inbound = SourceManager.ToInbound(new SourceEvent
        {
            SourceId = "mail",
            Text = "正文",
            SenderId = "boss@corp",
            DedupeKey = "mail:1234:7",
            Ext = new Dictionary<string, string> { ["from"] = "boss@corp", ["subject"] = "发版" },
        });

        // 外部事件没有频道：不会被按频道挂载的图看见
        Assert.Equal(string.Empty, inbound.ChannelKey);
        Assert.Equal(EventKind.External, inbound.Kind);
        Assert.Equal("mail", inbound.SourceId);
        Assert.Equal("boss@corp", inbound.SenderId);
        Assert.Equal("正文", inbound.Text);
        // 去重键原样带过：订阅分发靠它防重复唤醒
        Assert.Equal("mail:1234:7", inbound.DedupeKey);
        // 信源载荷进 Ext，键名大小写不敏感
        Assert.Equal("发版", inbound.Ext["Subject"]);
    }

    [Fact]
    public void SourceEventWithoutDedupeKeyStillGetsOne()
    {
        var inbound = SourceManager.ToInbound(new SourceEvent
        {
            SourceId = "mail",
            Text = "x",
            SenderId = "a@b",
        });

        Assert.False(string.IsNullOrWhiteSpace(inbound.DedupeKey));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // ── 帮手 ──

    private static SourceEvent Mail(string from, string body) => new()
    {
        SourceId = "mail",
        Text = body,
        SenderId = from,
        Ext = new Dictionary<string, string> { ["from"] = from },
        DedupeKey = "mail:" + body,
    };

    private sealed class FakeSource : IEventSource
    {
        public FakeSource(string id) => Id = id;

        public string Id { get; }

        public string Kind => "fake";

        public bool ThrowOnStart { get; init; }

        public bool ThrowOnStatus { get; init; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public event Func<SourceEvent, Task>? OnEvent
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken ct = default)
        {
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("boom");
            }

            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public EventSourceStatus GetStatus()
        {
            if (ThrowOnStatus)
            {
                throw new InvalidOperationException("boom-status");
            }

            return new EventSourceStatus { Id = Id, Kind = Kind, State = EventSourceState.Running };
        }
    }
}
