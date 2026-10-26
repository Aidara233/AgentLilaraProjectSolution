using AgentLilara.PluginSDK.Sources;
using MimeKit;
using Plugin.Email;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// T3：`PollOnceAsync` 的编排。
/// <para>这一层此前一行都测不了——它把 MailKit 的 <c>ImapClient</c> 直接塞进回调。
/// 抽出 <see cref="IMailboxSessionFactory"/> 之后，才能精确制造"UIDVALIDITY 变了""第 2 封解析失败"
/// "整个连接失败"这些真实会发生、而且表现极难排查的情况。</para>
/// </summary>
public class EmailPollOrchestrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-poll-" + Guid.NewGuid().ToString("N"));

    public EmailPollOrchestrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task FirstPollOnlyPrimesBaseline()
    {
        var factory = new FakeMailboxFactory
        {
            Head = new MailboxHead(UidValidity: 1, MaxUid: 5000),
        };

        var (source, received) = CreateSource(factory);

        await source.PollOnceAsync(CancellationToken.None);

        // 装好那一刻不该把积压的几百封未读全当成"刚到的"
        Assert.Empty(received);
        Assert.Equal(5000, Cursor()!.LastUid);
    }

    [Fact]
    public async Task NewMailIsDelivered()
    {
        WriteCursor(uidValidity: 1, lastUid: 100);

        var factory = new FakeMailboxFactory
        {
            Head = new MailboxHead(1, 103),
            Messages =
            {
                [101] = Mail(101),
                [102] = Mail(102),
                [103] = Mail(103),
            },
        };

        var (source, received) = CreateSource(factory);

        await source.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { 101L, 102L, 103L }, received.Select(e => long.Parse(e.Ext["uid"])));
        Assert.Equal(103, Cursor()!.LastUid);
    }

    [Fact]
    public async Task UidValidityChangeResetsWithoutDelivering()
    {
        WriteCursor(uidValidity: 1, lastUid: 100);

        var factory = new FakeMailboxFactory
        {
            // 服务器重建了邮箱：同号 UID 已经是另一封信，拿旧游标比会得出荒谬结论
            Head = new MailboxHead(UidValidity: 2, MaxUid: 30),
            Messages = { [25] = Mail(25), [30] = Mail(30) },
        };

        var (source, received) = CreateSource(factory);

        await source.PollOnceAsync(CancellationToken.None);

        Assert.Empty(received);
        var cursor = Cursor()!;
        Assert.Equal(2u, cursor.UidValidity);
        Assert.Equal(30, cursor.LastUid);
    }

    [Fact]
    public async Task FailedMessageDoesNotBlockTheRest()
    {
        WriteCursor(uidValidity: 1, lastUid: 100);

        var factory = new FakeMailboxFactory
        {
            Head = new MailboxHead(1, 103),
            Messages =
            {
                [101] = Mail(101),
                [103] = Mail(103),
            },
            FailFetchFor = { 102 },
        };

        var (source, received) = CreateSource(factory);

        await source.PollOnceAsync(CancellationToken.None);

        // 跳过坏的那封，其余照投
        Assert.Equal(new[] { 101L, 103L }, received.Select(e => long.Parse(e.Ext["uid"])));

        // 游标必须越过 102，否则每轮都会重试它、后面的永远收不到
        Assert.Equal(103, Cursor()!.LastUid);
    }

    [Fact]
    public async Task SessionFailurePropagatesAndLeavesCursorUntouched()
    {
        WriteCursor(uidValidity: 1, lastUid: 100);

        var factory = new FakeMailboxFactory
        {
            SessionError = new InvalidOperationException("连不上"),
        };

        var (source, _) = CreateSource(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.PollOnceAsync(CancellationToken.None));

        // 失败不能把游标往前推：没读过的东西不能算读过
        Assert.Equal(100, Cursor()!.LastUid);
    }

    [Fact]
    public async Task NoSubscriberDropsEventsSilently()
    {
        WriteCursor(uidValidity: 1, lastUid: 100);

        var factory = new FakeMailboxFactory
        {
            Head = new MailboxHead(1, 101),
            Messages = { [101] = Mail(101) },
        };

        var source = new EmailEventSource(factory, Config(), _root);

        // 没人订阅：不该抛
        await source.PollOnceAsync(CancellationToken.None);

        // 游标照常前进——不能因为没人听就反复重读同一封
        Assert.Equal(101, Cursor()!.LastUid);
    }

    [Fact]
    public async Task SubscriberExceptionDoesNotStopTheBatch()
    {
        WriteCursor(uidValidity: 1, lastUid: 100);

        var factory = new FakeMailboxFactory
        {
            Head = new MailboxHead(1, 102),
            Messages = { [101] = Mail(101), [102] = Mail(102) },
        };

        var source = new EmailEventSource(factory, Config(), _root);
        var attempts = 0;

        // 每一封都抛：一次投递故障不该吃掉整轮（这是 I1 里补上的隔离）
        source.OnEvent += _ =>
        {
            attempts++;
            throw new InvalidOperationException("投递挂了");
        };

        await source.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(102, Cursor()!.LastUid);
    }

    // ── 帮手 ──

    private (EmailEventSource Source, List<SourceEvent> Received) CreateSource(FakeMailboxFactory factory)
    {
        var source = new EmailEventSource(factory, Config(), _root);
        var received = new List<SourceEvent>();
        source.OnEvent += e =>
        {
            received.Add(e);
            return Task.CompletedTask;
        };

        return (source, received);
    }

    private static EmailEventSourceConfig Config() =>
        new() { Enabled = true, PollSeconds = 60, Folder = "INBOX" };

    private static MimeMessage Mail(long uid) => new()
    {
        From = { new MailboxAddress("someone", $"u{uid}@corp") },
        Subject = $"mail {uid}",
        Body = new TextPart("plain") { Text = $"body {uid}" },
    };

    private void WriteCursor(uint uidValidity, long lastUid) =>
        CursorStore().Save(new MailCursor { UidValidity = uidValidity, LastUid = lastUid });

    private MailCursor? Cursor() => CursorStore().Load();

    private MailCursorStore CursorStore() => new(Path.Combine(_root, "cursor.json"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>假会话工厂：能精确制造各种协议行为。</summary>
    private sealed class FakeMailboxFactory : IMailboxSessionFactory
    {
        public MailboxHead Head { get; set; } = new(1, 0);

        public Dictionary<long, MimeMessage> Messages { get; } = new();

        public HashSet<long> FailFetchFor { get; } = new();

        public Exception? SessionError { get; set; }

        public async Task<T> WithSessionAsync<T>(Func<IMailboxSession, Task<T>> work, CancellationToken ct)
        {
            if (SessionError is not null)
            {
                throw SessionError;
            }

            return await work(new FakeSession(this));
        }

        private sealed class FakeSession : IMailboxSession
        {
            private readonly FakeMailboxFactory _owner;

            public FakeSession(FakeMailboxFactory owner) => _owner = owner;

            public Task<MailboxHead> GetHeadAsync(CancellationToken ct) => Task.FromResult(_owner.Head);

            public Task<IReadOnlyList<long>> ListUidsAfterAsync(long afterUid, CancellationToken ct)
            {
                IReadOnlyList<long> uids = _owner.Messages.Keys
                    .Where(uid => uid > afterUid)
                    .OrderBy(uid => uid)
                    .ToList();

                return Task.FromResult(uids);
            }

            public Task<MimeMessage?> GetMessageAsync(long uid, CancellationToken ct)
            {
                if (_owner.FailFetchFor.Contains(uid))
                {
                    throw new IOException($"读第 {uid} 封失败");
                }

                return Task.FromResult(_owner.Messages.TryGetValue(uid, out var message) ? message : null);
            }
        }
    }
}
