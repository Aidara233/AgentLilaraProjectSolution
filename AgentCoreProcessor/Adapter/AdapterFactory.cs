using System;
using System.IO;
using AgentCoreProcessor.Config;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Adapter
{
    internal static class AdapterFactory
    {
        public static IAdapter Create(AdapterInstanceConfig config)
        {
            return config.Type.ToLowerInvariant() switch
            {
                "onebot" => CreateOneBot(config),
                "file" => CreateFile(config),
                _ => throw new ArgumentException($"Unknown adapter type: {config.Type}")
            };
        }

        private static OneBotAdapter CreateOneBot(AdapterInstanceConfig config)
        {
            var settings = config.Settings.ToObject<OneBotConfig>() ?? new OneBotConfig();
            return new OneBotAdapter(config.Id, settings);
        }

        private static FileAdapter CreateFile(AdapterInstanceConfig config)
        {
            var baseDir = config.Settings["baseDir"]?.ToString()
                ?? Path.Combine(PathConfig.StoragePath, "FileAdapter");
            var pollMs = config.Settings["pollIntervalMs"]?.Value<int>() ?? 2000;
            return new FileAdapter(config.Id, baseDir, pollMs);
        }
    }
}
