using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Adapter
{
    public interface IAdapter
    {
        string Platform { get; }
        event Action<IncomingMessage> OnMessageReceived;
        Task SendMessageAsync(OutgoingMessage message);
        Task StartAsync(CancellationToken ct = default);
        Task StopAsync();

        /// <summary>热重载配置。默认无操作，适配器按需覆盖。</summary>
        Task ReloadConfigAsync() => Task.CompletedTask;
    }
}
