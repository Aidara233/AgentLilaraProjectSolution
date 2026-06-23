// AgentCoreProcessor/Engine/Messaging/MessageEngineSpawnCheck.cs
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine.Messaging;

internal class MessageEngineSpawnCheck : IEngineSpawnCheck
{
    public string EngineType => "Message";

    public Task OnEventAsync(EngineEvent e, ISystemContext ctx) => Task.CompletedTask;

    public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
    {
        // MessageEngine 随 SystemEvent Started 启动一次
        if (e is SystemEvent se && se.Action == SystemAction.Started)
            return Task.FromResult(true);
        return Task.FromResult(false);
    }

    public ISubEngine Create(ISystemContext ctx) => new MessageEngine(ctx);
}
