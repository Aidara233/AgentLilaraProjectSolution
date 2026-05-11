using System.Collections.Generic;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    internal class ChannelConfig
    {
        public float Affinity { get; set; } = 1.0f;
        public string Importance { get; set; } = "normal";

        public int ActiveExtractionThreshold { get; set; } = 8;
        public int LurkingExtractionThreshold { get; set; } = 40;
        public bool AutoExtractionEnabled { get; set; } = true;

        [JsonProperty("extractionInterval")]
        private int ExtractionIntervalCompat { set => LurkingExtractionThreshold = value; }

        public List<string> WatchKeywords { get; set; } = new();
    }
}
