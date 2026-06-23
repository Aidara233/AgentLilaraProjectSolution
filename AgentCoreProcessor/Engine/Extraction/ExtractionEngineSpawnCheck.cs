// AgentCoreProcessor/Engine/Extraction/ExtractionEngineSpawnCheck.cs
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine.Extraction;

internal class ExtractionEngineSpawnCheck : IEngineSpawnCheck
{
    public string EngineType => "Extraction";

    public Task OnEventAsync(EngineEvent e, ISystemContext ctx) => Task.CompletedTask;

    public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
    {
        if (e is SystemEvent se && se.Action == SystemAction.Started)
            return Task.FromResult(true);
        return Task.FromResult(false);
    }

    public ISubEngine Create(ISystemContext ctx) => new ExtractionEngine(ctx);
}
