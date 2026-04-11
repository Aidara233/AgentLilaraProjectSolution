using Newtonsoft.Json;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 工具执行结果。
    /// </summary>
    public class ToolResult
    {
        [JsonProperty("toolId")]
        public string ToolId { get; set; } = string.Empty;

        /// <summary>"success"、"failed"、"skipped"</summary>
        [JsonProperty("status")]
        public string Status { get; set; } = "success";

        /// <summary>工具输出数据，成功时存入寄存器供下游引用。</summary>
        [JsonProperty("data")]
        public string? Data { get; set; }

        /// <summary>失败时的错误信息。</summary>
        [JsonProperty("error")]
        public string? Error { get; set; }

        [JsonIgnore]
        public bool IsSuccess => Status == "success";
    }
}
