using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Adapter
{
    public class AdapterManager
    {
        private readonly ConcurrentDictionary<string, IAdapter> adapters = new();
        private readonly ConcurrentDictionary<string, bool> enabledMap = new();
        private readonly EventBus? eventBus;
        private readonly string configDirectory;

        public AdapterManager(EventBus? eventBus = null)
        {
            this.eventBus = eventBus;
            this.configDirectory = Path.Combine(PathConfig.StoragePath, "Adapter");
        }

        // ── 配置驱动加载 ──

        public void LoadFromConfig()
        {
            if (!Directory.Exists(configDirectory)) return;

            // 旧配置迁移：如果存在 OneBotAdapter.json 且无 id 字段，转换为新格式
            MigrateLegacyConfig();

            foreach (var file in Directory.GetFiles(configDirectory, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var config = JsonConvert.DeserializeObject<AdapterInstanceConfig>(json);
                    if (config == null || string.IsNullOrEmpty(config.Id) || string.IsNullOrEmpty(config.Type))
                        continue;

                    var adapter = AdapterFactory.Create(config);
                    RegisterAdapter(adapter, config.Enabled);
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("AdapterManager", $"加载适配器配置失败 {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        private void MigrateLegacyConfig()
        {
            var legacyPath = Path.Combine(configDirectory, "OneBotAdapter.json");
            if (!File.Exists(legacyPath)) return;

            try
            {
                var json = File.ReadAllText(legacyPath);
                var legacy = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
                if (legacy == null || legacy.ContainsKey("id")) return;

                var newConfig = new AdapterInstanceConfig
                {
                    Id = "qq-main",
                    Type = "onebot",
                    Enabled = true,
                    Settings = legacy
                };

                var newPath = Path.Combine(configDirectory, "qq-main.json");
                File.WriteAllText(newPath, JsonConvert.SerializeObject(newConfig, Formatting.Indented));
                File.Delete(legacyPath);
                FrameworkLogger.Log("AdapterManager", "旧配置已迁移: OneBotAdapter.json → qq-main.json");
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("AdapterManager", $"配置迁移失败: {ex.Message}");
            }
        }

        // ── 注册 ──

        public void RegisterAdapter(IAdapter adapter, bool enabled = true)
        {
            adapter.OnMessageReceived += msg =>
            {
                msg.AdapterId = adapter.Id;
                eventBus?.PublishMessage(msg);
            };
            adapters[adapter.Id] = adapter;
            enabledMap[adapter.Id] = enabled;
        }

        // ── 动态生命周期 ──

        public async Task<bool> AddAsync(AdapterInstanceConfig config)
        {
            if (adapters.ContainsKey(config.Id)) return false;

            var adapter = AdapterFactory.Create(config);
            RegisterAdapter(adapter, config.Enabled);
            SaveConfig(config);

            if (config.Enabled)
                await adapter.StartAsync();

            return true;
        }

        public async Task<bool> RemoveAsync(string id)
        {
            if (!adapters.TryRemove(id, out var adapter)) return false;
            enabledMap.TryRemove(id, out _);

            await adapter.StopAsync();

            var configFile = Path.Combine(configDirectory, $"{id}.json");
            if (File.Exists(configFile)) File.Delete(configFile);

            return true;
        }

        public async Task<bool> EnableAsync(string id)
        {
            if (!adapters.TryGetValue(id, out var adapter)) return false;
            enabledMap[id] = true;
            UpdateConfigEnabled(id, true);
            await adapter.StartAsync();
            return true;
        }

        public async Task<bool> DisableAsync(string id)
        {
            if (!adapters.TryGetValue(id, out var adapter)) return false;
            enabledMap[id] = false;
            UpdateConfigEnabled(id, false);
            await adapter.StopAsync();
            return true;
        }

        public async Task StartAllAsync(CancellationToken ct = default)
        {
            var tasks = adapters
                .Where(kv => enabledMap.GetValueOrDefault(kv.Key, true))
                .Select(kv => kv.Value.StartAsync(ct));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public async Task StopAllAsync()
        {
            var tasks = adapters.Values.Select(a => a.StopAsync());
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // ── 路由 ──

        public async Task<string?> SendMessageAsync(string platform, OutgoingMessage message)
        {
            var adapter = ResolveForChannel(platform, message.ChannelId);
            if (adapter == null) return null;
            return await adapter.SendMessageAsync(message);
        }

        public async Task<string?> SendMessageByIdAsync(string adapterId, OutgoingMessage message)
        {
            if (!adapters.TryGetValue(adapterId, out var adapter)) return null;
            return await adapter.SendMessageAsync(message);
        }

        private IAdapter? ResolveForChannel(string platform, string channelId)
        {
            var candidates = adapters.Values
                .Where(a => a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase)
                            && enabledMap.GetValueOrDefault(a.Id, true))
                .ToList();

            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            // 多个候选：找 whitelist 包含目标频道的，或 blacklist 不包含的
            foreach (var c in candidates)
            {
                if (CanHandleChannel(c, channelId))
                    return c;
            }

            // 无精确匹配，fallback 到第一个
            return candidates[0];
        }

        private static bool CanHandleChannel(IAdapter adapter, string channelId)
        {
            if (adapter is OneBotAdapter oneBot)
            {
                var cfg = oneBot.GetConfig();
                if (cfg.FilterMode == "whitelist")
                    return cfg.Whitelist.Contains(channelId);
                else
                    return !cfg.Blacklist.Contains(channelId);
            }
            return true;
        }

        // ── 查询 ──

        public IAdapter? GetAdapter(string platform)
        {
            return adapters.Values.FirstOrDefault(a =>
                a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase)
                && enabledMap.GetValueOrDefault(a.Id, true));
        }

        public IAdapter? GetAdapterById(string id) => adapters.GetValueOrDefault(id);
        public List<IAdapter> GetAllAdapters() => adapters.Values.ToList();
        public List<AdapterStatus> GetAllStatuses() => adapters.Values.Select(a => a.GetStatus()).ToList();
        public List<string> GetRegisteredPlatforms() => adapters.Values.Select(a => a.Platform).Distinct().ToList();
        public bool IsEnabled(string id) => enabledMap.GetValueOrDefault(id, false);

        // ── 重载 ──

        public async Task<bool> ReloadAdapterAsync(string idOrPlatform)
        {
            var adapter = adapters.GetValueOrDefault(idOrPlatform)
                ?? GetAdapter(idOrPlatform);
            if (adapter == null) return false;
            await adapter.ReloadConfigAsync();
            return true;
        }

        // ── 高层语法糖 ──

        public async Task SetMainAdapterAsync(string platform, string id)
        {
            foreach (var kv in adapters)
            {
                if (!kv.Value.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (kv.Key == id)
                    await EnableAsync(kv.Key);
                else
                    await DisableAsync(kv.Key);
            }
        }

        // ── 持久化辅助 ──

        private void SaveConfig(AdapterInstanceConfig config)
        {
            Directory.CreateDirectory(configDirectory);
            var path = Path.Combine(configDirectory, $"{config.Id}.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
        }

        private void UpdateConfigEnabled(string id, bool enabled)
        {
            var path = Path.Combine(configDirectory, $"{id}.json");
            if (!File.Exists(path)) return;

            try
            {
                var json = File.ReadAllText(path);
                var config = JsonConvert.DeserializeObject<AdapterInstanceConfig>(json);
                if (config == null) return;
                config.Enabled = enabled;
                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
            }
            catch { }
        }
    }
}
