using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 工具调用的输入项，支持字面值和引用两种类型。
    /// </summary>
    public class ToolCallInput
    {
        /// <summary>"value" 表示字面值，"ref" 表示引用其他工具的输出。</summary>
        [JsonProperty("type")]
        public string Type { get; set; } = "value";

        /// <summary>当 type 为 "value" 时的字面值。</summary>
        [JsonProperty("value")]
        public string? Value { get; set; }

        /// <summary>当 type 为 "ref" 时引用的 toolId。</summary>
        [JsonProperty("source")]
        public string? Source { get; set; }

        [JsonIgnore]
        public bool IsRef => Type == "ref";
    }

    /// <summary>
    /// 表示一个工具调用的数据结构。
    /// 示例 JSON:
    /// {"tool":"文件流读取器","toolId":"read1","inputs":[{"type":"value","value":"example.txt"}],"output":"read1_out","outputToModel":false}
    /// </summary>
    public class ToolCall
    {
        [JsonProperty("tool")]
        public string Tool { get; set; } = string.Empty;

        [JsonProperty("toolId")]
        public string ToolId { get; set; } = string.Empty;

        [JsonProperty("inputs")]
        public List<ToolCallInput> Inputs { get; set; } = [];

        /// <summary>输出标识，存入寄存器供下游引用；无输出的工具可省略。</summary>
        [JsonProperty("output")]
        public string Output { get; set; } = string.Empty;

        /// <summary>是否将完整结果回传模型，默认 false。</summary>
        [JsonProperty("outputToModel")]
        public bool OutputToModel { get; set; } = false;

        /// <summary>是否在后续轮次的模型上下文中保留此工具的执行结果，默认 false。</summary>
        [JsonProperty("retain")]
        public bool Retain { get; set; } = false;

        /// <summary>
        /// 从 JSON 字符串创建 ToolCall 实例。
        /// </summary>
        public static ToolCall FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("json 参数不能为空", nameof(json));

            var obj = JsonConvert.DeserializeObject<ToolCall>(json);
            return obj ?? new ToolCall();
        }

        /// <summary>
        /// 将当前实例序列化为 JSON 字符串。
        /// </summary>
        public string ToJson()
        {
            return JsonConvert.SerializeObject(this);
        }

        /// <summary>
        /// 简单验证必填项，返回错误列表；若无错误则返回空集合。
        /// </summary>
        public IEnumerable<string> Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(Tool))
                errors.Add("字段 'tool' 不能为空。");
            if (string.IsNullOrWhiteSpace(ToolId))
                errors.Add("字段 'toolId' 不能为空。");

            return errors;
        }
    }
}
