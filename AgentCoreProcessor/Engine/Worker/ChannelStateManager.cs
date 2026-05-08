using System.Collections.Generic;
using System.IO;
using AgentCoreProcessor.Config;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    internal static class ChannelStateManager
    {
        private static readonly object _lock = new();

        private static string GetConfigPath(int channelId)
        {
            var dir = Path.Combine(PathConfig.StoragePath, "ChannelState");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"{channelId}.json");
        }

        public static ChannelConfig LoadConfig(int channelId, float? dbAffinity = null)
        {
            var path = GetConfigPath(channelId);
            lock (_lock)
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<ChannelConfig>(json) ?? new();
                }
            }

            var config = new ChannelConfig();
            if (dbAffinity.HasValue)
                config.Affinity = dbAffinity.Value;
            return config;
        }

        public static void SaveConfig(int channelId, ChannelConfig config)
        {
            var path = GetConfigPath(channelId);
            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            lock (_lock)
            {
                File.WriteAllText(path, json);
            }
        }
    }
}
