using System.Collections.Generic;
using AgentCoreProcessor.Models;
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

        /// <summary>附带的图片（工具返回图片时使用，如"查看图片"工具）。</summary>
        [JsonIgnore]
        public List<ContentPart>? Attachments { get; set; }

        [JsonIgnore]
        public bool IsSuccess => Status == "success";
    }
}
