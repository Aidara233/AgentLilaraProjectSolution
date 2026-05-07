using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Adapter
{
    public class AdapterInstanceConfig
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public JObject Settings { get; set; } = new();
    }
}
