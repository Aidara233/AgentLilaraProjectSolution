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

        /// <summary>按平台名查找适配器。</summary>
        public IAdapter? GetAdapter(string platform)
            => adapters.FirstOrDefault(a => a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase));

        /// <summary>获取所有已注册的适配器平台名。</summary>
        public List<string> GetRegisteredPlatforms()
            => adapters.Select(a => a.Platform).ToList();

        /// <summary>热重载指定适配器的配置。</summary>
        public async Task<bool> ReloadAdapterAsync(string platform)
        {
            var adapter = GetAdapter(platform);
            if (adapter == null) return false;
            await adapter.ReloadConfigAsync();
            return true;
        }

        public async Task<string?> SendMessageAsync(string platform, OutgoingMessage message)
        {
            var adapter = adapters.FirstOrDefault(a =>
                a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase))
                ?? adapters.FirstOrDefault(); // fallback：平台不匹配时用第一个适配器

            if (adapter != null)
            {
                return await adapter.SendMessageAsync(message).ConfigureAwait(false);
            }
            return null;
        }
    }
}
