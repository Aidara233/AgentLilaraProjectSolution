using System.Collections.Generic;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 工具调用的数据结构。模型输出 JSON，框架解析执行。
    /// 示例: {"tool":"读取文件","inputs":["example.txt","4000"]}
    /// </summary>
    public class ToolCall
    {
        [JsonProperty("tool")]
        public string Tool { get; set; } = string.Empty;

        [JsonProperty("inputs")]
        public List<string> Inputs { get; set; } = [];

        /// <summary>框架内部使用：本轮中的序号。</summary>
        [JsonIgnore]
        public int Index { get; set; }

        /// <summary>原生工具调用的 tool_use_id（Anthropic tool_use / OpenAI function_call id）。文本 JSON 路径为 null。</summary>
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
