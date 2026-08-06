// AgentCoreProcessor.Tests/SessionAccessImplTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.MCP;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// SessionAccessImpl 测试（不依赖 LLM/DB）。
/// 用 TestHost 预置 fake session，验证 list 字段映射与 SendMessage 的定位/注入/唤醒。
/// </summary>
public class SessionAccessImplTests
{
    private sealed class TestContext : ISystemContext
    {
        public SessionEngine? SessionEngine { get; set; }
        public SessionManager Session => null!;
        public IEmbeddingProvider Embedding => null!;
        public IVisionProvider? Vision => null;
        public IOcrProvider? Ocr => null;
        public AdapterManager Adapters => null!;
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

    private static (SessionEngine engine, SessionAccessImpl access) Create(string sessionId, bool alive)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession(sessionId, alive, registerInPool: false);
        var ctx = new TestContext { SessionEngine = engine };
        var access = new SessionAccessImpl(ctx);
        return (engine, access);
    }

    // ── 1. 列表：活跃 session 字段映射 ──

    [Fact]
    public async Task ListSessions_ReturnsActiveSessionFields()
    {
        var (_, access) = Create("s-occ", alive: true);

        var sessions = await access.ListSessionsAsync();
        var s = Assert.Single(sessions);

        Assert.Equal("s-occ", s.SessionId);
        Assert.Equal("Running", s.Status);
        Assert.Equal("", s.Type);          // fake session 未设 Type
        Assert.Null(s.Channel);            // 未 claim 频道
        Assert.Null(s.Parent);
        Assert.Equal(0, s.Rounds);
        Assert.Equal("", s.Core);          // fake session 未设 active core
    }

    [Fact]
    public async Task ListSessions_ExcludesDeadSessions()
    {
        // alive=false 的 fake session 不进 GetActiveSessions
        var (_, access) = Create("s-dead", alive: false);

        var sessions = await access.ListSessionsAsync();
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task ListSessions_NoEngine_ReturnsEmpty()
    {
        var access = new SessionAccessImpl(new TestContext());
        Assert.Empty(await access.ListSessionsAsync());
    }

    // ── 2. 发消息：定位 + 注入 + 唤醒 ──

    [Fact]
    public void SendMessage_UnknownTarget_ReturnsFalse()
    {
        var (_, access) = Create("s-occ", alive: true);
        Assert.False(access.SendMessageAsync("s-nope", "s-sender", "你好"));
    }

    [Fact]
    public void SendMessage_DeadTarget_ReturnsFalse()
    {
        var (_, access) = Create("s-dead", alive: false);
        Assert.False(access.SendMessageAsync("s-dead", "s-sender", "你好"));
    }

    [Fact]
    public void SendMessage_ToLiveSession_InjectsAndWakes()
    {
        var (engine, access) = Create("s-occ", alive: true);
        var session = TestHost.GetFakeSession(engine, "s-occ");

        // fake session 未初始化 _pendingInjections/_wakeSignaled，反射预置
        var injField = typeof(RuntimeSession).GetField("_pendingInjections",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        injField.SetValue(session, new List<Message>());
        var wakeField = typeof(RuntimeSession).GetField("_wakeSignaled",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        wakeField.SetValue(session, false);

        var ok = access.SendMessageAsync("s-occ", "s-sender", "你好");

        Assert.True(ok);
        var injected = (List<Message>)injField.GetValue(session)!;
        var msg = Assert.Single(injected);
        Assert.Equal("[会话 s-sender 消息] 你好", msg.Content);
        Assert.Equal("user", msg.Role);
        Assert.True((bool)wakeField.GetValue(session)!);
    }

    // ── 3. 无引擎时发送返回 false，不抛错 ──

    [Fact]
    public void SendMessage_NoEngine_ReturnsFalse()
    {
        var access = new SessionAccessImpl(new TestContext());
        Assert.False(access.SendMessageAsync("s-occ", "s-sender", "你好"));
    }
}
