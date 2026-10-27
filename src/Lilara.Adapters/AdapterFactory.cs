using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Adapter
{
    internal sealed class AdapterFactory
    {
        private readonly string _storagePath;
        private readonly IAdapterMediaStore _media;
        private readonly Func<string, Task<(string LocalPath, string Hash)>> _archive;
        private readonly Func<DateTime, DateTime> _toLocal;

        public AdapterFactory(string storagePath, IAdapterMediaStore media,
            Func<string, Task<(string LocalPath, string Hash)>> archive, Func<DateTime, DateTime> toLocal)
        {
            _storagePath = storagePath;
            _media = media;
            _archive = archive;
            _toLocal = toLocal;
        }

        public IAdapter Create(AdapterInstanceConfig config)
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

        private OneBotAdapter CreateOneBot(AdapterInstanceConfig config)
        {
            var settings = config.Settings.ToObject<OneBotConfig>() ?? new OneBotConfig();
            return new OneBotAdapter(config.Id, settings, _media);
        }

        private FileAdapter CreateFile(AdapterInstanceConfig config)
        {
            // baseDir = 输入根目录（UI 的 input_dir），其下 input/ 与 output/ 用于收发。
            // outputDir 未配置时沿用 baseDir/output；配置后按独立输出目录使用。
            var baseDir = config.Settings["baseDir"]?.ToString()
                ?? config.Settings["input_dir"]?.ToString()
                ?? Path.Combine(_storagePath, "FileAdapter");
            var outputDir = config.Settings["outputDir"]?.ToString()
                ?? config.Settings["output_dir"]?.ToString();
            var pollMs = config.Settings["pollIntervalMs"]?.Value<int>()
                ?? config.Settings["poll_ms"]?.Value<int>()
                ?? 2000;
            return new FileAdapter(config.Id, baseDir, pollMs, outputDir, _archive);
        }

        private HttpBridgeAdapter CreateHttp(AdapterInstanceConfig config)
        {
            var settings = config.Settings.ToObject<HttpBridgeConfig>() ?? new HttpBridgeConfig();
            return new HttpBridgeAdapter(config.Id, settings, Path.Combine(_storagePath, "Adapter", config.Id + ".json"), _toLocal);
        }

        private IcalinguaAdapter CreateIcalingua(AdapterInstanceConfig config)
        {
            var settings = config.Settings.ToObject<IcalinguaConfig>() ?? new IcalinguaConfig();
            return new IcalinguaAdapter(config.Id, settings, _media);
        }
    }
}
