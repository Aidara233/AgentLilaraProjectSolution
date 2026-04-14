using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Adapter
{
    public class AdapterManager
    {
        private readonly List<IAdapter> adapters = new();
        private readonly EventBus? eventBus;

        public AdapterManager(EventBus? eventBus = null)
        {
            this.eventBus = eventBus;
        }

        public void RegisterAdapter(IAdapter adapter)
        {
            adapter.OnMessageReceived += msg => eventBus?.PublishMessage(msg);
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
                a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase))
                ?? adapters.FirstOrDefault(); // fallback：平台不匹配时用第一个适配器

            if (adapter != null)
            {
                await adapter.SendMessageAsync(message).ConfigureAwait(false);
            }
        }
    }
}
