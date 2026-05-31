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

        [JsonProperty("refineTriggerCount")]
        public int RefineTriggerCount { get; set; } = 3;

        [JsonProperty("phase1Concurrency")]
        public int Phase1Concurrency { get; set; } = 5;

        [JsonProperty("phase2Concurrency")]
        public int Phase2Concurrency { get; set; } = 2;

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
