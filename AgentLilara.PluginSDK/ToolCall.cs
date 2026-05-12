using System.Collections.Generic;
using Newtonsoft.Json;

namespace AgentLilara.PluginSDK
{
    /// <summary>
    /// 工具调用数据结构。
    /// </summary>
    public class ToolCall
    {
        [JsonProperty("tool")]
        public string Tool { get; set; } = string.Empty;

        [JsonProperty("inputs")]
        public List<string> Inputs { get; set; } = [];

        [JsonIgnore]
        public int Index { get; set; }

        /// <summary>原生工具调用的 tool_use_id。文本 JSON 路径为 null。</summary>
        [JsonIgnore]
        public string? ToolUseId { get; set; }

        public static ToolCall FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new ToolCall();
            return JsonConvert.DeserializeObject<ToolCall>(json) ?? new ToolCall();
        }

        public IEnumerable<string> Validate()
        {
            if (string.IsNullOrWhiteSpace(Tool))
                yield return "字段 'tool' 不能为空。";
        }
    }
}
