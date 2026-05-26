using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Adapter
{
    public class AdapterManager
    {
        private readonly ConcurrentDictionary<string, IAdapter> adapters = new();
        private readonly ConcurrentDictionary<string, bool> enabledMap = new();
        private readonly ConcurrentDictionary<string, AdapterInstanceConfig> configs = new();
        private readonly EventBus? eventBus;
        private readonly string configDirectory;

        public event Action? OnAdaptersChanged;

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

                    configs[config.Id] = config;
                    var adapter = AdapterFactory.Create(config);
                    RegisterAdapter(adapter, config.Enabled);
                }
                catch (Exception)
                {
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
            }
            catch (Exception)
            {
            }
        }

        // ── 注册 ──

        public void RegisterAdapter(IAdapter adapter, bool enabled = true)
        {
            adapter.OnMessageReceived += msg =>
            {
                msg.AdapterId = adapter.Id;
                using var ctx = Signal.Begin(
                    LogGroup.Adapter,
                    $"adapter:{adapter.Id}",
                    "消息接收",
                    new { adapterId = adapter.Id, platform = adapter.Platform, channelId = msg.ChannelId, userId = msg.PlatformUserId, isPrivate = msg.IsPrivate }
                );
                eventBus?.PublishMessage(msg);
            };
            adapters[adapter.Id] = adapter;
            enabledMap[adapter.Id] = enabled;
        }

        // ── 动态生命周期 ──

        public async Task<bool> AddAsync(AdapterInstanceConfig config)
        {
            if (adapters.ContainsKey(config.Id)) return false;

            configs[config.Id] = config;
            var adapter = AdapterFactory.Create(config);
            RegisterAdapter(adapter, config.Enabled);
            SaveConfig(config);

            if (config.Enabled)
                await adapter.StartAsync();

            OnAdaptersChanged?.Invoke();
            return true;
        }

        public async Task<bool> RemoveAsync(string id)
        {
            if (!adapters.TryRemove(id, out var adapter)) return false;
            enabledMap.TryRemove(id, out _);
            configs.TryRemove(id, out _);

            await adapter.StopAsync();

            var configFile = Path.Combine(configDirectory, $"{id}.json");
            if (File.Exists(configFile)) File.Delete(configFile);

            OnAdaptersChanged?.Invoke();
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

        public async Task StartAllAsync(bool debugMode = false, CancellationToken ct = default)
        {
            var tasks = adapters
                .Where(kv =>
                {
                    if (!enabledMap.GetValueOrDefault(kv.Key, true)) return false;
                    if (!configs.TryGetValue(kv.Key, out var cfg)) return true;
                    if (!cfg.AutoStart) return false;
                    if (debugMode && !cfg.AutoStartDebug) return false;
                    return true;
                })
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
            using var span = Signal.Open(
                LogGroup.Adapter,
                "消息发送",
                new { adapterId = adapter.Id, platform, channelId = message.ChannelId }
            );
            var result = await adapter.SendMessageAsync(message);
            span.SetCloseDetail(new { messageId = result });
            return result;
        }

        public async Task<string?> SendMessageByIdAsync(string adapterId, OutgoingMessage message)
        {
            if (!adapters.TryGetValue(adapterId, out var adapter)) return null;
            using var span = Signal.Open(
                LogGroup.Adapter,
                "消息发送",
                new { adapterId, channelId = message.ChannelId }
            );
            var result = await adapter.SendMessageAsync(message);
            span.SetCloseDetail(new { messageId = result });
            return result;
        }

        public async Task<ActionResult> ExecuteActionAsync(string platform, string channelId, string action, Dictionary<string, string> parameters)
        {
            var adapter = ResolveForChannel(platform, channelId);
            if (adapter == null)
                return new ActionResult { Success = false, Error = "无可用适配器" };
            return await adapter.ExecuteActionAsync(action, parameters);
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

        /// <summary>按频道标识推断适配器（无需平台信息）。</summary>
        public IAdapter? ResolveByChannelId(string channelId)
        {
            var enabled = adapters.Values
                .Where(a => enabledMap.GetValueOrDefault(a.Id, true))
                .ToList();
            if (enabled.Count == 0) return null;
            if (enabled.Count == 1) return enabled[0];
            foreach (var a in enabled)
            {
                if (CanHandleChannel(a, channelId)) return a;
            }
            return enabled[0];
        }

        public IAdapter? GetAdapter(string platform)
        {
            return adapters.Values.FirstOrDefault(a =>
                a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase)
                && enabledMap.GetValueOrDefault(a.Id, true));
        }

        public IAdapter? GetAdapterById(string id) => adapters.GetValueOrDefault(id);
        public List<IAdapter> GetAllAdapters() => adapters.Values.ToList();
        public List<AdapterStatus> GetAllStatuses() => adapters.Values.Select(a => a.GetStatus()).ToList();

        public string? GetBotPlatformId(string platform)
        {
            var adapter = adapters.Values.FirstOrDefault(a =>
                a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase));
            return adapter?.BotPlatformId;
        }
        public List<string> GetRegisteredPlatforms() => adapters.Values.Select(a => a.Platform).Distinct().ToList();
        public bool IsEnabled(string id) => enabledMap.GetValueOrDefault(id, false);
        public AdapterInstanceConfig? GetConfigById(string id) => configs.GetValueOrDefault(id);
        public List<AdapterInstanceConfig> GetAllConfigs() => configs.Values.ToList();

        public void UpdateConfig(AdapterInstanceConfig config)
        {
            configs[config.Id] = config;
            SaveConfig(config);
        }

        // ── 重载 ──

        public async Task<bool> ReloadAdapterAsync(string idOrPlatform)
        {
            var adapter = adapters.GetValueOrDefault(idOrPlatform)
                ?? GetAdapter(idOrPlatform);
            if (adapter == null) return false;
            return await adapter.ReloadConfigAsync();
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
            catch { Signal.Warn(LogGroup.Adapter, "适配器配置保存失败"); }
        }
    }
}
