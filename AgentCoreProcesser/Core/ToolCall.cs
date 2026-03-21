using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AgentCoreProcesser.Core
{
    /// <summary>
    /// 表示一个工具调用的数据结构，字段名与提供的 JSON 对应。
    /// 示例 JSON:
    /// {"tool":"工具1","toolId":"...","pipeIn":"...","pipeOut":"...","afterThan":["id1","id2"],"input":"..."}
    /// </summary>
    public class ToolCall
    {
        [JsonProperty("tool")]
        public string Tool { get; set; } = string.Empty;

        [JsonProperty("toolId")]
        public string ToolId { get; set; } = string.Empty;

        [JsonProperty("pipeIn")]
        public string PipeIn { get; set; } = string.Empty;

        [JsonProperty("pipeOut")]
        public string PipeOut { get; set; } = string.Empty;

        [JsonProperty("afterThan")]
        public List<string> AfterThan { get; set; } = [];

        [JsonProperty("input")]
        public string Input { get; set; } = string.Empty;

        /// <summary>
        /// 快速判断输出管道是否为模型返回（"<Model>"），不区分大小写。
        /// </summary>
        [JsonIgnore]
        public bool IsOutputToModel => string.Equals(PipeOut, "<Model>", StringComparison.OrdinalIgnoreCase);

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
            // pipeIn/pipeOut 可能为空，取决于工具类型，但做基本长度检查
            if (PipeIn != null && PipeIn.Length > 1024)
                errors.Add("字段 'pipeIn' 太长。");
            if (PipeOut != null && PipeOut.Length > 1024)
                errors.Add("字段 'pipeOut' 太长。");
            // afterThan 长度限制示例
            if (AfterThan != null && AfterThan.Count > 1000)
                errors.Add("字段 'afterThan' 包含过多项。");

            return errors;
        }
    }
}
