using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Adapter
{
    public interface IAdapter
    {
        string Id { get; }
        string Platform { get; }
        string? BotPlatformId => null;
        event Action<IncomingMessage> OnMessageReceived;
        Task<string?> SendMessageAsync(OutgoingMessage message);
        Task StartAsync(CancellationToken ct = default);
        Task StopAsync();
        Task ReloadConfigAsync() => Task.CompletedTask;
        AdapterStatus GetStatus();
        Task<ActionResult> ExecuteActionAsync(string action, Dictionary<string, string> parameters) =>
            Task.FromResult(new ActionResult { Success = false, Error = "Not supported" });
        List<AdapterAction> GetAvailableActions() => new();
    }
}
