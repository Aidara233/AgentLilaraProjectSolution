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
                "http" => CreateHttp(config),
                "icalingua" => CreateIcalingua(config),
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
            // baseDir = 输入根目录（UI 的 input_dir），其下 input/ 与 output/ 用于收发。
            // outputDir 未配置时沿用 baseDir/output；配置后按独立输出目录使用。
            var baseDir = config.Settings["baseDir"]?.ToString()
                ?? config.Settings["input_dir"]?.ToString()
                ?? Path.Combine(PathConfig.StoragePath, "FileAdapter");
            var outputDir = config.Settings["outputDir"]?.ToString()
                ?? config.Settings["output_dir"]?.ToString();
            var pollMs = config.Settings["pollIntervalMs"]?.Value<int>()
                ?? config.Settings["poll_ms"]?.Value<int>()
                ?? 2000;
            return new FileAdapter(config.Id, baseDir, pollMs, outputDir);
        }

        private static HttpBridgeAdapter CreateHttp(AdapterInstanceConfig config)
        {
            var settings = config.Settings.ToObject<HttpBridgeConfig>() ?? new HttpBridgeConfig();
            return new HttpBridgeAdapter(config.Id, settings);
        }

        private static IcalinguaAdapter CreateIcalingua(AdapterInstanceConfig config)
        {
            var settings = config.Settings.ToObject<IcalinguaConfig>() ?? new IcalinguaConfig();
            return new IcalinguaAdapter(config.Id, settings);
        }
    }
}
