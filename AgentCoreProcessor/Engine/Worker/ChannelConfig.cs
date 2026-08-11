using System.Collections.Generic;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    internal class ChannelConfig
    {
        public float Affinity { get; set; } = 1.0f;
        public string Importance { get; set; } = "normal";

        public List<string> WatchKeywords { get; set; } = new();
    }
}
