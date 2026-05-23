using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Component;

internal class SubAgentAccessAdapter : ISubAgentAccess
{
    private readonly Engine.MasterEngine _master;

    public SubAgentAccessAdapter(Engine.MasterEngine master)
    {
        _master = master;
    }

    public SubAgentInfo Create(string instruction)
    {
        var systemEngine = _master.GetSystemEngine();
        if (systemEngine == null)
            return new SubAgentInfo { SessionId = "", IsAlive = false };

        var session = systemEngine.CreateSubAgent(instruction);
        return new SubAgentInfo { SessionId = session.SessionId, IsAlive = session.IsAlive };
    }

    public SubAgentInfo Create(string instruction, string? delegationId)
    {
        var systemEngine = _master.GetSystemEngine();
        if (systemEngine == null)
            return new SubAgentInfo { SessionId = "", IsAlive = false };

        var session = systemEngine.CreateSubAgentForDelegation(instruction, delegationId);
        return new SubAgentInfo { SessionId = session.SessionId, IsAlive = session.IsAlive };
    }

    public SubAgentInfo? Get(string sessionId)
    {
        var systemEngine = _master.GetSystemEngine();
        var session = systemEngine?.GetSubAgent(sessionId);
        if (session == null) return null;
        return new SubAgentInfo { SessionId = session.SessionId, IsAlive = session.IsAlive };
    }

    public async Task<bool> SendInstructionAsync(string sessionId, string instruction)
    {
        var systemEngine = _master.GetSystemEngine();
        var session = systemEngine?.GetSubAgent(sessionId);
        if (session == null) return false;
        await session.SendInstructionAsync(instruction);
        return true;
    }

    public void RequestStop(string sessionId)
    {
        var systemEngine = _master.GetSystemEngine();
        var session = systemEngine?.GetSubAgent(sessionId);
        session?.RequestStop();
    }
}
