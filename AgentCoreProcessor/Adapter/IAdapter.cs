using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Adapter
{
    public interface IAdapter
    {
        string Id { get; }
        string Platform { get; }
        event Action<IncomingMessage> OnMessageReceived;
        Task<string?> SendMessageAsync(OutgoingMessage message);
        Task StartAsync(CancellationToken ct = default);
        Task StopAsync();
        Task ReloadConfigAsync() => Task.CompletedTask;
        AdapterStatus GetStatus();
    }
}
