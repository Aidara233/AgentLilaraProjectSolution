using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.MCP
{
    internal class McpToolOverride
    {
        [JsonProperty("permission")]
        public string? Permission { get; set; }

        [JsonProperty("continueLoop")]
        public bool? ContinueLoop { get; set; }

        [JsonProperty("retainResult")]
        public bool? RetainResult { get; set; }
    }

    internal class McpServerEntry
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("transport")]
        public string Transport { get; set; } = "stdio";

        [JsonProperty("command")]
        public string? Command { get; set; }

        [JsonProperty("args")]
        public List<string> Args { get; set; } = new();

        [JsonProperty("env")]
        public Dictionary<string, string> Env { get; set; } = new();

        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("toolGroup")]
        public string? ToolGroup { get; set; }

        [JsonProperty("defaultExpanded")]
        public bool DefaultExpanded { get; set; } = false;

        [JsonProperty("permission")]
        public string Permission { get; set; } = "Default";

        [JsonProperty("toolPrefix")]
        public string? ToolPrefix { get; set; }

        [JsonProperty("timeout")]
        public int Timeout { get; set; } = 30;

        [JsonProperty("toolOverrides")]
        public Dictionary<string, McpToolOverride> ToolOverrides { get; set; } = new();
    }

    internal class McpConfig
    {
        [JsonProperty("servers")]
        public List<McpServerEntry> Servers { get; set; } = new();

        public static McpConfig Load(string path)
        {
            if (!File.Exists(path)) return new McpConfig();
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<McpConfig>(json) ?? new McpConfig();
        }
    }
}