using System.Text.Json.Nodes;

namespace AgentCoreProcessor.Tool
{
    public class ToolDefinition
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public JsonNode Parameters { get; set; } = new JsonObject();
    }
}
