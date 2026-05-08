using System.Collections.Generic;

namespace AgentCoreProcessor.Engine
{
    internal class ChannelConfig
    {
        public float Affinity { get; set; } = 1.0f;
        public string Importance { get; set; } = "normal";
        public int ExtractionInterval { get; set; } = 200;
        public List<string> WatchKeywords { get; set; } = new();
    }
}
