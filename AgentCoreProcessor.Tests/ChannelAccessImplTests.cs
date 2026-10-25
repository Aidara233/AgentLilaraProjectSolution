// AgentCoreProcessor.Tests/ChannelAccessImplTests.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.MCP;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// ChannelAccessImpl 查询链路测试（不依赖 LLM）。
/// 临时 SQLite → 真实 SessionManager + SessionEngine → 验证频道统计/占用填充与聊天记录查询。
/// </summary>
public class ChannelAccessImplTests
{
    private static string TempDbPath()
        => Path.Combine(Path.GetTempPath(), $"lilara_ch_{Guid.NewGuid():N}.db");

    private sealed class TestServices
    {
        public DbManager Db = null!;
        public ChannelRepository Channels = null!;
        public MessageRepository Messages = null!;
        public SessionManager Session = null!;
        public SessionEngine SessionEngine = null!;
        public ChannelAccessImpl Access = null!;
    }

    /// <summary>
    /// ISystemContext 最小实现：提供真实 SessionManager + SessionEngine 快照，
    /// 其余成员保持 null/default（ChannelAccessImpl 只用这两个）。
    /// </summary>
    private sealed class TestContext : ISystemContext
    {
        public SessionManager Session { get; set; } = null!;
        public SessionEngine? SessionEngine { get; set; }
        public IEmbeddingProvider Embedding => null!;
        public IVisionProvider? Vision => null;
        public IOcrProvider? Ocr => null;
        public AdapterManager Adapters => null!;
        public AgentCoreProcessor.Engine.Graph.GraphManager? Graphs => null;
        public EventBus EventBus => null!;
        public ImpulseConfig ImpulseConfig => null!;
        public bool IsIdle => true;
        public TimeSpan IdleDuration => TimeSpan.Zero;
        public DateTime LastMessageTime => DateTime.MinValue;
        public bool HasActiveEngine(string engineType) => false;
        public int GetActiveEngineCount(string engineType) => 0;
        public ISubEngine StartEngine(ISubEngine engine) => engine;
        public void RequestStopEngine(ISubEngine engine) { }
        public List<(string Type, int Count)> GetActiveEngineSummary() => new();
        public void RequestStopEnginesByType(string engineType) { }
        public List<ISubEngine> GetActiveEnginesSnapshot()
            => SessionEngine == null ? new List<ISubEngine>() : new List<ISubEngine> { SessionEngine };
        public bool MuteMode { get; set; }
        public SleepState CurrentSleepState { get; set; }
        public string? CurrentDreamPhase { get; set; }
        public DateTime? DreamStartTime { get; set; }
        public McpServerManager? McpManager => null;
        public ModuleBus ModuleBus => null!;
        public GlobalComponentHost? GlobalComponentHost => null;
        public IServiceProvider ComponentServices => null!;
        public Tool.Host.PluginLoader PluginLoader => null!;
        public Tool.Host.ToolContextImpl ToolContext => null!;
        public ClaimRepository Claims => null!;
        public JournalRepository Journals => null!;
        public KeywordRepository Keywords => null!;
        public FragmentKeywordRepository FragmentKeywords => null!;
        public EmotionSnapshotRepository EmotionSnapshots => null!;
        public FrameworkEventRepository FrameworkEvents => null!;
        public ConflictGroupRepository ConflictGroups => null!;
        public ReviewQueueRepository ReviewQueue => null!;
        public AgentCoreProcessor.Engine.Mind.MindEngine? Mind => null;
    }

    private static async Task<TestServices> CreateServicesAsync()
    {
        var db = new DbManager(TempDbPath());
        await db.InitAsync();

        var channels = new ChannelRepository(db);
        var messages = new MessageRepository(db);
        var persons = new PersonRepository(db);
        var session = new SessionManager(
            new UserRepository(db, persons), persons,
            channels, messages);

        // fake session 占用 channel 1（TestHost 基建：注册进资源池 + Full claim）
        var sessionEngine = TestHost.BuildSessionEngineWithFakeSession(
            "s-occupying", alive: true, registerInPool: true);

        var ctx = new TestContext { Session = session, SessionEngine = sessionEngine };
        var access = new ChannelAccessImpl(ctx);
        return new TestServices
        {
            Db = db, Channels = channels, Messages = messages,
            Session = session, SessionEngine = sessionEngine, Access = access
        };
    }

    private static async Task<Channel> CreateChannelAsync(TestServices s, string name)
        => await s.Channels.FindOrCreateAsync(name);

    private static async Task<UserMessage> InsertMessageAsync(
        TestServices s, int channelId, string content, DateTime time,
        string sender = "UserA", bool isBot = false)
    {
        var msg = new UserMessage
        {
            ChannelId = channelId,
            Content = content,
            SenderName = sender,
            Time = time,
            IsFromBot = isBot
        };
        await s.Messages.SaveAsync(msg);
        return msg;
    }

    // ── 1. 频道列表：统计 + 最后活跃 + 会话占用填充 ──

    [Fact]
    public async Task ListChannels_ReturnsStatsAndOccupancy()
    {
        var s = await CreateServicesAsync();

        // 频道 1 被 fake session 占用（id=1 与 TestHost claim 的 channel 1 对齐）
        var chA = await CreateChannelAsync(s, "group_a");
        Assert.Equal(1, chA.Id);
        var t1 = new DateTime(2026, 8, 6, 10, 0, 0);
        var t2 = new DateTime(2026, 8, 6, 11, 0, 0);
        await InsertMessageAsync(s, chA.Id, "第一句", t1);
        await InsertMessageAsync(s, chA.Id, "第二句", t2, sender: "Lilara", isBot: true);

        // 频道 2 无占用
        var chB = await CreateChannelAsync(s, "group_b");
        await InsertMessageAsync(s, chB.Id, "别的群", new DateTime(2026, 8, 6, 12, 0, 0));

        var list = await s.Access.GetAllChannelsAsync();
        var a = list.Single(c => c.Name == "group_a");
        var b = list.Single(c => c.Name == "group_b");

        Assert.Equal(2, a.MessageCount);
        Assert.Equal(t2, a.LastActive);
        Assert.Equal("s-occupying", a.SessionId);
        Assert.True(a.HasActiveEngine);

        Assert.Equal(1, b.MessageCount);
        Assert.Equal(new DateTime(2026, 8, 6, 12, 0, 0), b.LastActive);
        Assert.Null(b.SessionId);
        Assert.False(b.HasActiveEngine);
    }

    // ── 2. 关键词搜索：命中 + 升序 ──

    [Fact]
    public async Task SearchChat_Keyword_ReturnsAscendingMatches()
    {
        var s = await CreateServicesAsync();
        var ch = await CreateChannelAsync(s, "kw_channel");

        var t1 = new DateTime(2026, 8, 6, 9, 0, 0);
        var t2 = new DateTime(2026, 8, 6, 9, 30, 0);
        await InsertMessageAsync(s, ch.Id, "今天天气不错", t1);
        await InsertMessageAsync(s, ch.Id, "聊点别的", new DateTime(2026, 8, 6, 9, 10, 0));
        await InsertMessageAsync(s, ch.Id, "天气真好啊", t2, sender: "Lilara", isBot: true);

        var msgs = await s.Access.SearchChatAsync(new ChannelQuery
        {
            ChannelNames = new List<string> { "kw_channel" },
            Keyword = "天气",
            Limit = 10
        });

        Assert.Equal(2, msgs.Count);
        Assert.Equal("今天天气不错", msgs[0].Content);
        Assert.Equal("天气真好啊", msgs[1].Content);
        Assert.True(msgs[0].Timestamp < msgs[1].Timestamp);
        Assert.False(msgs[0].IsBot);
        Assert.True(msgs[1].IsBot);
    }

    // ── 3. 游标模式：afterId 增量 / beforeId 翻旧 ──

    [Fact]
    public async Task SearchChat_AfterId_ReturnsNewerMessages()
    {
        var s = await CreateServicesAsync();
        var ch = await CreateChannelAsync(s, "cur_channel");

        var m1 = await InsertMessageAsync(s, ch.Id, "第一条", new DateTime(2026, 8, 6, 9, 0, 0));
        await InsertMessageAsync(s, ch.Id, "第二条", new DateTime(2026, 8, 6, 9, 5, 0));
        await InsertMessageAsync(s, ch.Id, "第三条", new DateTime(2026, 8, 6, 9, 10, 0));

        var after = await s.Access.SearchChatAsync(new ChannelQuery
        {
            ChannelNames = new List<string> { "cur_channel" },
            AfterId = m1.Id,
            Limit = 10
        });
        Assert.Equal(2, after.Count);
        Assert.Equal("第二条", after[0].Content);
        Assert.Equal("第三条", after[1].Content);
    }

    [Fact]
    public async Task SearchChat_BeforeId_ReturnsOlderMessages()
    {
        var s = await CreateServicesAsync();
        var ch = await CreateChannelAsync(s, "cur_channel");

        await InsertMessageAsync(s, ch.Id, "第一条", new DateTime(2026, 8, 6, 9, 0, 0));
        await InsertMessageAsync(s, ch.Id, "第二条", new DateTime(2026, 8, 6, 9, 5, 0));
        var m3 = await InsertMessageAsync(s, ch.Id, "第三条", new DateTime(2026, 8, 6, 9, 10, 0));

        var before = await s.Access.SearchChatAsync(new ChannelQuery
        {
            ChannelNames = new List<string> { "cur_channel" },
            BeforeId = m3.Id,
            Limit = 10
        });
        // beforeId 语义为 Id <= beforeId（含锚点自身，与 HttpBridge 一致）
        Assert.Equal(3, before.Count);
        Assert.Equal("第一条", before[0].Content);
        Assert.Equal("第二条", before[1].Content);
        Assert.Equal("第三条", before[2].Content);
    }

    // ── 4. 时间范围过滤 ──

    [Fact]
    public async Task SearchChat_TimeRange_Filters()
    {
        var s = await CreateServicesAsync();
        var ch = await CreateChannelAsync(s, "time_channel");

        await InsertMessageAsync(s, ch.Id, "早上的", new DateTime(2026, 8, 6, 9, 0, 0));
        await InsertMessageAsync(s, ch.Id, "中午的", new DateTime(2026, 8, 6, 12, 0, 0));
        await InsertMessageAsync(s, ch.Id, "晚上的", new DateTime(2026, 8, 6, 19, 0, 0));

        var msgs = await s.Access.SearchChatAsync(new ChannelQuery
        {
            ChannelNames = new List<string> { "time_channel" },
            TimeFrom = new DateTime(2026, 8, 6, 10, 0, 0),
            TimeTo = new DateTime(2026, 8, 6, 18, 0, 0),
            Limit = 10
        });

        var only = Assert.Single(msgs);
        Assert.Equal("中午的", only.Content);
    }

    // ── 5. 省略频道：跨频道/全频道搜索 ──

    [Fact]
    public async Task SearchChat_NoChannel_SearchesAll()
    {
        var s = await CreateServicesAsync();
        var chA = await CreateChannelAsync(s, "all_a");
        var chB = await CreateChannelAsync(s, "all_b");

        await InsertMessageAsync(s, chA.Id, "a 里有目标词", new DateTime(2026, 8, 6, 9, 0, 0));
        await InsertMessageAsync(s, chB.Id, "b 里也有目标词", new DateTime(2026, 8, 6, 9, 5, 0));

        var msgs = await s.Access.SearchChatAsync(new ChannelQuery
        {
            Keyword = "目标词",
            Limit = 10
        });
        Assert.Equal(2, msgs.Count);
    }

    // ── 6. 无效频道名 → 空结果，不抛错 ──

    [Fact]
    public async Task SearchChat_UnknownChannel_ReturnsEmpty()
    {
        var s = await CreateServicesAsync();

        var msgs = await s.Access.SearchChatAsync(new ChannelQuery
        {
            ChannelNames = new List<string> { "不存在的频道" },
            Limit = 10
        });
        Assert.Empty(msgs);
    }
}
