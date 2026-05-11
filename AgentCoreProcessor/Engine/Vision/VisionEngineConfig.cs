using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Vision
{
    internal class VisionEngineConfig
    {
        [JsonProperty("visionConcurrency")]
        public int VisionConcurrency { get; set; } = 3;

        [JsonProperty("ocrConcurrency")]
        public int OcrConcurrency { get; set; } = 4;

        [JsonProperty("visionRetryCount")]
        public int VisionRetryCount { get; set; } = 2;

        [JsonProperty("visionRetryDelayMs")]
        public int VisionRetryDelayMs { get; set; } = 3000;

        [JsonProperty("batchSize")]
        public int BatchSize { get; set; } = 10;

        [JsonProperty("ocrEnabled")]
        public bool OcrEnabled { get; set; } = true;

        [JsonProperty("visionEnabled")]
        public bool VisionEnabled { get; set; } = true;

        [JsonProperty("ocrRichTextThreshold")]
        public int OcrRichTextThreshold { get; set; } = 80;

        private static string ConfigPath => Path.Combine(
            Config.PathConfig.StoragePath, "Engine", "VisionEngineConfig.json");

        public static VisionEngineConfig Load()
        {
            if (!File.Exists(ConfigPath))
            {
                var cfg = new VisionEngineConfig();
                cfg.Save();
                return cfg;
            }
            try
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonConvert.DeserializeObject<VisionEngineConfig>(json) ?? new VisionEngineConfig();
            }
            catch
            {
                return new VisionEngineConfig();
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
