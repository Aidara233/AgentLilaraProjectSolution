// AgentCoreProcessor/Component/SubAgentAccessAdapter.cs
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Component;

internal class SubAgentAccessAdapter : ISubAgentAccess
{
    private readonly Engine.MasterEngine _master;

    public SubAgentAccessAdapter(Engine.MasterEngine master)
    {
        _master = master;
    }

    private SessionEngine? GetSessionEngine()
    {
        var engines = _master.GetActiveEnginesSnapshot();
        return engines.OfType<SessionEngine>().FirstOrDefault();
    }

    public SubAgentInfo Create(string instruction)
    {
        var se = GetSessionEngine();
        if (se == null) return new SubAgentInfo { SessionId = "", IsAlive = false };

        var session = se.CreateSession(new AgentLilara.PluginSDK.CreateSessionSpec
        {
            ComponentIds = new[] { "basic-tools", "file-tools", "memory-tools" },
            InitialState = instruction
        });
        return new SubAgentInfo { SessionId = session.SessionId, IsAlive = session.IsAlive };
    }

    public SubAgentInfo Create(string instruction, string? delegationId)
    {
        var se = GetSessionEngine();
        if (se == null) return new SubAgentInfo { SessionId = "", IsAlive = false };

        var session = se.CreateSession(new AgentLilara.PluginSDK.CreateSessionSpec
        {
            ComponentIds = new[] { "basic-tools", "file-tools", "memory-tools" },
            InitialState = instruction,
            ParentSessionId = delegationId
        });
        return new SubAgentInfo { SessionId = session.SessionId, IsAlive = session.IsAlive };
    }

    public SubAgentInfo? Get(string sessionId)
    {
        var se = GetSessionEngine();
        var session = se?.GetSession(sessionId);
        if (session == null) return null;
        return new SubAgentInfo { SessionId = session.SessionId, IsAlive = session.IsAlive };
    }

    public Task<bool> SendInstructionAsync(string sessionId, string instruction)
    {
        var se = GetSessionEngine();
        var session = se?.GetSession(sessionId);
        if (session == null) return Task.FromResult(false);
        // 通过输入端口注入指令
        return Task.FromResult(true);
    }

    public void RequestStop(string sessionId)
    {
        var se = GetSessionEngine();
        se?.StopSession(sessionId, "requested by parent");
    }

    public List<SubAgentInfo> List()
    {
        var se = GetSessionEngine();
        if (se == null) return new List<SubAgentInfo>();
        return se.GetActiveSessions()
            .Select(s => new SubAgentInfo { SessionId = s.SessionId, IsAlive = s.IsAlive })
            .ToList();
    }
}
