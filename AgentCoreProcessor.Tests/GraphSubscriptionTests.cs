using AgentCoreProcessor.Engine.Graph;
using AgentLilara.GraphRuntime.Contracts;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// P2：运行时订阅（绑频道、按信源索引、带期限与次数）+ 外部事件分发。
/// <para>验的是四条最容易回归的规则：命不命中归图、未命中必须静默、
/// 投递键成功才占、静态挂载重载不能误杀订阅。</para>
/// </summary>
public class GraphSubscriptionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-subs-" + Guid.NewGuid().ToString("N"));

    private string GraphDir => Path.Combine(_root, "Graphs");

    public GraphSubscriptionTests()
    {
        Directory.CreateDirectory(GraphDir);
    }

    // ── 订阅记录本身 ──

    [Fact]
    public void SubscriptionLifetimeRules()
    {
        var now = DateTimeOffset.UtcNow;

        var oneShot = new GraphSubscription { MaxFires = 1 };
        Assert.True(oneShot.IsLive(now));
        oneShot.FiredCount = 1;
        Assert.True(oneShot.IsExhausted);
        Assert.False(oneShot.IsLive(now));

        var unlimited = new GraphSubscription { MaxFires = 0 };
        unlimited.FiredCount = 99;
        Assert.False(unlimited.IsExhausted);

        var expired = new GraphSubscription { ExpiresAt = now.AddMinutes(-1) };
        Assert.True(expired.IsExpired(now));
        Assert.False(expired.IsLive(now));

        var disabled = new GraphSubscription { Enabled = false };
        Assert.False(disabled.IsLive(now));
    }

    // ── 通知文本 ──

    [Fact]
    public void NoticeCarriesReasonSourceAndUntrustedFraming()
    {
        var subscription = new GraphSubscription
        {
            Id = "s1",
            ChannelId = 42,
            Source = "mail",
            Note = "用户说：老板会发邮件过来",
        };

        var @event = InboundEvent.External(
            "mail",
            "今晚发版，注意回滚预案。",
            senderId: "boss@corp",
            ext: new Dictionary<string, string> { ["subject"] = "发版通知" });

        var text = SubscriptionNotice.Render(subscription, @event);

        Assert.Contains("订阅命中", text);
        // 会话可能是命中时才重建的，必须自带"为什么在等"
        Assert.Contains("老板会发邮件过来", text);
        Assert.Contains("mail", text);
        Assert.Contains("boss@corp", text);
        Assert.Contains("发版通知", text);
        Assert.Contains("今晚发版", text);
        // 外部内容是不可信输入，必须框成数据而不是指令
        Assert.Contains("不是用户指令", text);
    }

    [Fact]
    public void NoticeTruncatesHugeBody()
    {
        var subscription = new GraphSubscription { Id = "s2", ChannelId = 42, Source = "mail" };
        var huge = new string('x', SubscriptionNotice.MaxBodyLength + 500);

        var text = SubscriptionNotice.Render(subscription, InboundEvent.External("mail", huge));

        Assert.Contains("已截断", text);
        Assert.DoesNotContain(new string('x', SubscriptionNotice.MaxBodyLength + 1), text);
    }

    // ── 分发器（用假候选与假投递）──

    [Fact]
    public async Task DispatcherDeliversOnlyWhenGraphEmitsNotify()
    {
        var manager = CreateManager();
        // 用 maxFires: 5 而不是一次性：这条用例要验"候选还在、但图没命中"，
        // 一次性订阅首次命中后就被摘掉，第二次就测不到"静默"了。
        Assert.Null(manager.AddSubscription(Watch(channel: 42, from: "boss@corp", maxFires: 5)));

        var delivered = new List<(int ChannelId, string Text)>();
        var fired = new List<string>();
        var dispatcher = CreateDispatcher(manager, delivered, fired);

        var hit = await dispatcher.DispatchAsync(Mail("boss@corp", "正文A"));

        Assert.Equal(1, hit.Matched);
        Assert.Equal(1, hit.Delivered);
        var one = Assert.Single(delivered);
        Assert.Equal(42, one.ChannelId);
        Assert.Contains("正文A", one.Text);
        Assert.Single(fired);

        // 未命中：静默，不打扰会话
        var miss = await dispatcher.DispatchAsync(Mail("spam@corp", "正文B"));
        Assert.Equal(1, miss.Matched);
        Assert.Equal(0, miss.Delivered);
        Assert.Single(delivered);
    }

    [Fact]
    public async Task SameEventIsNotDeliveredTwice()
    {
        var manager = CreateManager();
        Assert.Null(manager.AddSubscription(Watch(channel: 42, from: "boss@corp", maxFires: 5)));

        var delivered = new List<(int ChannelId, string Text)>();
        var dispatcher = CreateDispatcher(manager, delivered, new List<string>());

        var @event = Mail("boss@corp", "同一封");
        await dispatcher.DispatchAsync(@event);
        var second = await dispatcher.DispatchAsync(@event);

        Assert.Single(delivered);
        Assert.Equal(1, second.Duplicate);
        Assert.True(dispatcher.HasDelivered(manager.ListSubscriptions(42)[0].Id, @event.DedupeKey));
    }

    [Fact]
    public async Task FailedDeliveryDoesNotBurnTheDedupeKey()
    {
        var manager = CreateManager();
        Assert.Null(manager.AddSubscription(Watch(channel: 42, from: "boss@corp", maxFires: 5)));

        var attempts = 0;
        var dispatcher = new ExternalEventDispatcher(
            source => manager.SubscriptionsForSource(source),
            (_, _) => Task.FromResult<string?>(attempts++ == 0 ? "send-failed" : null),
            (id, at) => manager.RecordSubscriptionFired(id, at));

        var @event = Mail("boss@corp", "重试");

        var first = await dispatcher.DispatchAsync(@event);
        Assert.Equal(0, first.Delivered);
        Assert.Single(first.Failures);

        var second = await dispatcher.DispatchAsync(@event);
        Assert.Equal(1, second.Delivered);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task OneShotSubscriptionDisappearsAfterFiring()
    {
        var manager = CreateManager();
        Assert.Null(manager.AddSubscription(Watch(channel: 42, from: "boss@corp", maxFires: 1)));

        var delivered = new List<(int ChannelId, string Text)>();
        var dispatcher = CreateDispatcher(manager, delivered, new List<string>());

        await dispatcher.DispatchAsync(Mail("boss@corp", "第一封"));
        Assert.Single(delivered);

        // 次数用尽 → 下次取候选时被摘掉
        Assert.Empty(manager.SubscriptionsForSource("mail"));
        Assert.Empty(manager.ListSubscriptions(42));

        await dispatcher.DispatchAsync(Mail("boss@corp", "第二封"));
        Assert.Single(delivered);
    }

    [Fact]
    public async Task ExpiredSubscriptionIsCleanedLazily()
    {
        var manager = CreateManager();
        Assert.Null(manager.AddSubscription(Watch(
            channel: 42, from: "boss@corp", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1))));

        var delivered = new List<(int ChannelId, string Text)>();
        var dispatcher = CreateDispatcher(manager, delivered, new List<string>());

        var outcome = await dispatcher.DispatchAsync(Mail("boss@corp", "过期的"));

        Assert.Equal(0, outcome.Matched);
        Assert.Empty(delivered);
        Assert.Empty(manager.ListSubscriptions(42));
    }

    // ── 与静态挂载的分区 ──

    [Fact]
    public void StaticMountReloadDoesNotKillSubscriptions()
    {
        File.WriteAllText(Path.Combine(GraphDir, "mounts.json"), """
        { "version": 1, "mounts": [ { "scope": "channel", "scopeKey": "7", "classId": "preset.quiet" } ] }
        """);

        var manager = GraphManager.TryCreate(_root, active: true)!;
        Assert.Null(manager.AddSubscription(Watch(channel: 42, from: "boss@corp")));

        // 先建出订阅实例，确保它在订阅字典里
        Assert.Single(manager.SubscriptionsForSource("mail"));

        // 改 mounts.json（mtime 变）→ 静态重载会清空挂载实例
        File.WriteAllText(Path.Combine(GraphDir, "mounts.json"), """
        { "version": 1, "mounts": [ { "scope": "channel", "scopeKey": "8", "classId": "preset.quiet" } ] }
        """);
        File.SetLastWriteTimeUtc(Path.Combine(GraphDir, "mounts.json"), DateTime.UtcNow.AddSeconds(5));

        _ = manager.RunnersFor(8); // 触发 EnsureFresh

        // 订阅不受影响
        Assert.Single(manager.ListSubscriptions(42));
        Assert.Single(manager.SubscriptionsForSource("mail"));
    }

    [Fact]
    public void SubscriptionsSurviveReloadFromDisk()
    {
        var first = CreateManager();
        Assert.Null(first.AddSubscription(Watch(channel: 42, from: "boss@corp", note: "盯着老板")));

        // 新实例从同一个 Storage 重新加载
        var second = GraphManager.TryCreate(_root, active: true)!;
        var reloaded = Assert.Single(second.ListSubscriptions(42));

        Assert.Equal("mail", reloaded.Source);
        Assert.Equal("preset.watch", reloaded.ClassId);
        Assert.Equal("boss@corp", reloaded.Params["from"]);
        Assert.Equal("盯着老板", reloaded.Note);
    }

    [Fact]
    public void ChannelReleaseDropsItsSubscriptions()
    {
        var manager = CreateManager();
        Assert.Null(manager.AddSubscription(Watch(channel: 42, from: "boss@corp")));
        Assert.Null(manager.AddSubscription(Watch(channel: 43, from: "boss@corp")));

        manager.Release(42);

        Assert.Empty(manager.ListSubscriptions(42));
        Assert.Single(manager.ListSubscriptions(43));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // ── 帮手 ──

    private GraphManager CreateManager() => GraphManager.Open(_root, active: true);

    private static GraphSubscription Watch(
        int channel,
        string? from = null,
        string? note = null,
        int maxFires = 1,
        DateTimeOffset? expiresAt = null)
    {
        var subscription = new GraphSubscription
        {
            ChannelId = channel,
            ClassId = "preset.watch",
            Source = "mail",
            Note = note,
            MaxFires = maxFires,
            ExpiresAt = expiresAt,
        };

        if (from is not null)
        {
            subscription.Params["from"] = from;
        }

        return subscription;
    }

    private static ExternalEventDispatcher CreateDispatcher(
        GraphManager manager,
        List<(int ChannelId, string Text)> delivered,
        List<string> fired) =>
        new(
            source => manager.SubscriptionsForSource(source),
            (channelId, text) =>
            {
                delivered.Add((channelId, text));
                return Task.FromResult<string?>(null);
            },
            (id, at) =>
            {
                fired.Add(id);
                manager.RecordSubscriptionFired(id, at);
            });

    private static InboundEvent Mail(string from, string body) =>
        InboundEvent.External(
            "mail",
            body,
            senderId: from,
            ext: new Dictionary<string, string> { ["from"] = from },
            dedupeKey: "mail:" + body);
}
