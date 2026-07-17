// AgentCoreProcessor.Tests/TestHost.cs
using System;
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
using AgentCoreProcessor.Memory;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 测试辅助：构造 SessionEngine 实例并预置内部状态。
/// 使用反射注入 _sessions / _forwards 等私有字段，避免污染生产代码。
/// </summary>
internal static class TestHost
{
    /// <summary>构造一个 SessionEngine 实例，预置一个 fake session，用于 ResolveActiveSession 等纯状态测试。</summary>
    public static SessionEngine BuildSessionEngineWithFakeSession(string sessionId, bool alive)
    {
        var ctx = new FakeSystemContext();
        var engine = new SessionEngine(ctx);

        if (alive)
        {
            var session = CreateFakeRuntimeSession(sessionId, SessionStatus.Running);
            GetSessions(engine)[sessionId] = session;
        }

        return engine;
    }

    /// <summary>在 engine 的 _forwards 字典中注入一条转发记录。</summary>
    public static void SeedForward(this SessionEngine engine, string oldId, string newId)
    {
        GetForwards(engine)[oldId] = newId;
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

    private static ConcurrentDictionary<string, RuntimeSession> GetSessions(SessionEngine engine)
    {
        var field = typeof(SessionEngine).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException("SessionEngine._sessions 字段未找到");
        return (ConcurrentDictionary<string, RuntimeSession>)field.GetValue(engine)!;
    }

    private static ConcurrentDictionary<string, string> GetForwards(SessionEngine engine)
    {
        var field = typeof(SessionEngine).GetField("_forwards",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException("SessionEngine._forwards 字段未找到");
        return (ConcurrentDictionary<string, string>)field.GetValue(engine)!;
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
        public MemoryRepository Memories => null!;
        public TempMemoryRepository TempMemories => null!;
        public MemoryLinkRepository MemoryLinks => null!;
        public MemoryService MemorySvc => null!;
        public PersonaMemoryRepository PersonaMemories => null!;
        public SessionManager Session => null!;
        public IEmbeddingProvider Embedding => null!;
        public IVisionProvider? Vision => null;
        public IOcrProvider? Ocr => null;
        public AdapterManager Adapters => null!;
        public EventBus EventBus => null!;
        public BeaconRepository Beacons => null!;
        public PersonTraitRepository PersonTraits => null!;
        public DreamLogRepository DreamLogs => null!;
        public ReviewLogRepository ReviewLogs => null!;
        public EvaluationScoreRepository EvaluationScores => null!;
        public ImpulseConfig ImpulseConfig => null!;
        public TrustProgressionConfig TrustConfig => null!;
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
        public DelegationBus DelegationBus => null!;
        public ModuleBus ModuleBus => null!;
        public GlobalComponentHost? GlobalComponentHost => null;
        public IServiceProvider ComponentServices => null!;
        public Tool.Host.PluginLoader PluginLoader => null!;
        public Tool.Host.ToolContextImpl ToolContext => null!;
        public SignalFilterManager SignalFilters => null!;
    }
}
