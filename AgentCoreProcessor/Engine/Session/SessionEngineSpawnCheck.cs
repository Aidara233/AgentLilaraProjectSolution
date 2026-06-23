// AgentCoreProcessor/Engine/Session/SessionEngineSpawnCheck.cs
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine.Session;

internal class SessionEngineSpawnCheck : IEngineSpawnCheck
{
    public string EngineType => "Session";

    public Task OnEventAsync(EngineEvent e, ISystemContext ctx) => Task.CompletedTask;

    public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
    {
        if (e is SystemEvent se && se.Action == SystemAction.Started)
            return Task.FromResult(true);
        return Task.FromResult(false);
    }

    public ISubEngine Create(ISystemContext ctx) => new SessionEngine(ctx);
}
