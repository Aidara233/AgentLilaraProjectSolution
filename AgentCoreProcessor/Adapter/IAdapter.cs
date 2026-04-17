using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Adapter
{
    public interface IAdapter
    {
        string Platform { get; }
        event Action<IncomingMessage> OnMessageReceived;
        /// <summary>发送消息，返回平台侧消息ID（用于数据库关联）。不支持时返回 null。</summary>
        Task<string?> SendMessageAsync(OutgoingMessage message);
        Task StartAsync(CancellationToken ct = default);
        Task StopAsync();

        /// <summary>热重载配置。默认无操作，适配器按需覆盖。</summary>
        Task ReloadConfigAsync() => Task.CompletedTask;
    }
}
