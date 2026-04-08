using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Adapter
{
    public class AdapterManager
    {
        private readonly List<IAdapter> adapters = new();

        public event Action<IncomingMessage>? OnMessageReceived;

        public void RegisterAdapter(IAdapter adapter)
        {
            adapter.OnMessageReceived += msg => OnMessageReceived?.Invoke(msg);
            adapters.Add(adapter);
        }

        public async Task StartAllAsync(CancellationToken ct = default)
        {
            var tasks = adapters.Select(a => a.StartAsync(ct));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public async Task StopAllAsync()
        {
            var tasks = adapters.Select(a => a.StopAsync());
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public async Task SendMessageAsync(string platform, OutgoingMessage message)
        {
            var adapter = adapters.FirstOrDefault(a =>
                a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase));

            if (adapter != null)
            {
                await adapter.SendMessageAsync(message).ConfigureAwait(false);
            }
        }
    }
}
