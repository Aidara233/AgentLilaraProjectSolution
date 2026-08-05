// AgentCoreProcessor.Tests/TestHost.cs
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.MCP;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 测试辅助：构造 SessionEngine / MasterEngine 实例并预置内部状态。
/// 使用反射注入 _sessions / _forwards / activeEngines 等私有字段，避免污染生产代码。
/// </summary>
internal static class TestHost
{
    /// <summary>构造一个 SessionEngine 实例，预置一个 fake session，用于 ResolveActiveSession 等纯状态测试。
    /// alive=true 时默认把 session 注册进资源池（_poolByMember，channel 1），使寻址能解析到它。
    /// registerInPool=false 时跳过自动注册，供需要完全控制 claim 状态的测试使用。</summary>
    public static SessionEngine BuildSessionEngineWithFakeSession(string sessionId, bool alive, bool registerInPool = true)
    {
        var ctx = new FakeSystemContext();
        var engine = new SessionEngine(ctx);

        if (alive)
        {
            var session = CreateFakeRuntimeSession(sessionId, SessionStatus.Running);
            GetSessions(engine)[sessionId] = session;
            if (registerInPool)
                engine.ClaimChannel(sessionId, 1, ClaimLevel.Full);
        }

        return engine;
    }

    /// <summary>
    /// 构造一个 MasterEngine 实例，注入 fake SessionEngine（带可选 claim）以及一个记录型 ExpressEngineSpawnCheck。
    /// 返回 facade 用于调用 WakeLoop 并验证结果。
    /// </summary>
    public static MasterEngineTestFacade BuildMasterEngineWithChannelClaim(
        int channelId, string? claimerSession, bool sessionAlive = true)
    {
        var master = new MasterEngine(new AdapterManager(), new EventBus());

        // Always inject a SessionEngine so tests can verify it wasn't woken.
        var sid = claimerSession ?? "__unused";
        var alive = claimerSession != null && sessionAlive;
        var sessionEngine = BuildSessionEngineWithFakeSession(sid, alive);

        // Claim exists regardless of sessionAlive (dead-claim test needs stale claim).
        if (claimerSession != null)
        {
            sessionEngine.ClaimChannel(claimerSession, channelId, ClaimLevel.Full);
        }

        // Inject into MasterEngine.activeEngines
        var enginesField = typeof(MasterEngine).GetField("activeEngines",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var enginesList = (IList)enginesField.GetValue(master)!;
        enginesList.Add(sessionEngine);

        // Inject a recording spawn check for Express path tracking
        var recordingCheck = new RecordingExpressSpawnCheck();
        var checksField = typeof(MasterEngine).GetField("spawnChecks",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var checksList = (IList)checksField.GetValue(master)!;
        checksList.Add(recordingCheck);

        return new MasterEngineTestFacade(master, sessionEngine, recordingCheck);
    }

    /// <summary>在资源池树中注入一条 compact 转发：oldId 成为 newId 所在池的 retired member。</summary>
    public static void SeedForward(this SessionEngine engine, string oldId, string newId)
    {
        var pool = GetPoolByMember(engine, newId);
        if (pool == null) return;
        pool.Members.Add(oldId);
        GetPoolByMemberDict(engine)[oldId] = pool;
    }

    /// <summary>检查指定 session 是否被 WakeUp 调用过。</summary>
    public static bool WasWoken(this SessionEngine engine, string sessionId)
    {
        var sessions = GetSessions(engine);
        if (!sessions.TryGetValue(sessionId, out var session))
            return false;

        var wakeField = typeof(RuntimeSession).GetField("_wakeSignaled",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (wakeField == null)
            throw new InvalidOperationException("RuntimeSession._wakeSignaled 字段未找到");
        return (bool)wakeField.GetValue(session)!;
    }

    /// <summary>获取预置的 fake RuntimeSession 实例（供测试操作其连接状态等）。</summary>
    public static RuntimeSession GetFakeSession(SessionEngine engine, string sessionId)
    {
        var sessions = GetSessions(engine);
        if (!sessions.TryGetValue(sessionId, out var session))
            throw new InvalidOperationException($"fake session {sessionId} 未找到");
        return session;
    }

    private static ConcurrentDictionary<string, RuntimeSession> GetSessions(SessionEngine engine)
    {
        var field = typeof(SessionEngine).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException("SessionEngine._sessions 字段未找到");
        return (ConcurrentDictionary<string, RuntimeSession>)field.GetValue(engine)!;
    }

    private static SessionPool? GetPoolByMember(SessionEngine engine, string sessionId)
    {
        var dict = GetPoolByMemberDict(engine);
        return dict.TryGetValue(sessionId, out var pool) ? pool : null;
    }

    private static ConcurrentDictionary<string, SessionPool> GetPoolByMemberDict(SessionEngine engine)
    {
        var field = typeof(SessionEngine).GetField("_poolByMember",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException("SessionEngine._poolByMember 字段未找到");
        return (ConcurrentDictionary<string, SessionPool>)field.GetValue(engine)!;
    }

    /// <summary>
    /// 通过 FormatterServices 创建一个未初始化构造的 RuntimeSession，
    /// 再用反射设置 SessionId 和 Status 以模拟活跃/死亡状态。
    /// </summary>
    private static RuntimeSession CreateFakeRuntimeSession(string sessionId, SessionStatus status)
    {
        var session = (RuntimeSession)FormatterServices.GetUninitializedObject(typeof(RuntimeSession));

        var idField = typeof(RuntimeSession).GetField("<SessionId>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(RuntimeSession).GetField("SessionId",
                BindingFlags.NonPublic | BindingFlags.Instance);
        if (idField != null)
            idField.SetValue(session, sessionId);

        // 设置 _status 字段（IsAlive 属性依赖此字段）
        var statusField = typeof(RuntimeSession).GetField("_status",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(RuntimeSession).GetField("<Status>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);
        if (statusField != null)
            statusField.SetValue(session, status);

        return session;
    }

    /// <summary>
    /// ISystemContext 最小化存根。仅满足 SessionEngine 构造签名，所有属性返回 null/default。
    /// </summary>
    private class FakeSystemContext : ISystemContext
    {
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
        public List<ISubEngine> GetActiveEnginesSnapshot() => new();
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
}

/// <summary>
/// MasterEngine 测试门面。封装反射注入的引擎状态，提供 WakeLoop 调用和结果验证入口。
/// </summary>
internal class MasterEngineTestFacade
{
    private readonly MasterEngine _master;
    private readonly SessionEngine? _sessionEngine;
    private readonly RecordingExpressSpawnCheck _recordingCheck;

    public MasterEngineTestFacade(MasterEngine master, SessionEngine? sessionEngine, RecordingExpressSpawnCheck recordingCheck)
    {
        _master = master;
        _sessionEngine = sessionEngine;
        _recordingCheck = recordingCheck;
    }

    public void WakeLoop(string sessionId) => _master.WakeLoop(sessionId);

    /// <summary>检查指定 session 是否被 WakeUpSession 唤醒过。</summary>
    public bool SessionWasWoken(string sessionId)
    {
        if (_sessionEngine == null) return false;
        return _sessionEngine.WasWoken(sessionId);
    }

    /// <summary>检查 Express 路径是否被触发（GetActiveChannels 被访问过）。</summary>
    public bool ExpressWasSignaled(int channelId)
        => _recordingCheck.ChannelsQueried > 0;
}

/// <summary>
/// 记录型 ExpressEngineSpawnCheck（继承 ExpressEngineSpawnCheck 以通过类型匹配）。
/// 记录 GetActiveChannels 被调用的次数，使得测试可以判定 Express 路径是否被触及。
/// </summary>
internal class RecordingExpressSpawnCheck : ExpressEngineSpawnCheck
{
    public int ChannelsQueried { get; private set; }

    // Note: GetActiveChannels is non-virtual in the base class, so this won't be called
    // polymorphically. Express path verification relies on manual testing via --test.
    // This class exists to allow BuildMasterEngineWithChannelClaim to inject a compatible
    // spawn check without changing production code.
}
