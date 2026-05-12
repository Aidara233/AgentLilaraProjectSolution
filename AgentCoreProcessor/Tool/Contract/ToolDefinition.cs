using System.Text.Json.Nodes;

namespace AgentCoreProcessor.Tool.Contract
{
    /// <summary>
    /// 原生工具定义（供 Anthropic Tools / OpenAI Functions 使用）。
    /// </summary>
    public class ToolDefinition
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public JsonNode Parameters { get; set; } = new JsonObject();
    }
}
