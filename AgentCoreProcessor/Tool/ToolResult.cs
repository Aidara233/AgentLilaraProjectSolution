using Newtonsoft.Json;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 工具执行结果。
    /// </summary>
    public class ToolResult
    {
        /// <summary>"success"、"failed"、"skipped"</summary>
        [JsonProperty("status")]
        public string Status { get; set; } = "success";

        /// <summary>工具输出数据。</summary>
        [JsonProperty("data")]
        public string? Data { get; set; }

        /// <summary>失败时的错误信息。</summary>
        [JsonProperty("error")]
        public string? Error { get; set; }

        [JsonIgnore]
        public bool IsSuccess => Status == "success";
    }
}
