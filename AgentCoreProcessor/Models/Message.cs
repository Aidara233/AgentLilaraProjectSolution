using Newtonsoft.Json;

namespace AgentCoreProcessor.Models
{
    public class Message
    {
        [JsonProperty("role")]
        public string Role { get; set; } = "user"; // system, user, assistant

        [JsonProperty("content")]
        public string Content { get; set; } = "";

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string? Name { get; set; }
    }
}