// AgentCoreProcessor/Engine/AdapterEng/AdapterEngineSpawnCheck.cs
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine.AdapterEng;

internal class AdapterEngineSpawnCheck : IEngineSpawnCheck
{
    public string EngineType => "Adapter";

    public Task OnEventAsync(EngineEvent e, ISystemContext ctx) => Task.CompletedTask;

    public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
    {
        if (e is SystemEvent se && se.Action == SystemAction.Started)
            return Task.FromResult(true);
        return Task.FromResult(false);
    }

    public ISubEngine Create(ISystemContext ctx) => new AdapterEngine();
}
