using System.Collections.Generic;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Tool.Contract
{
    /// <summary>
    /// 工具执行结果。
    /// </summary>
    public class ToolResult
    {
        [JsonProperty("status")]
        public string Status { get; set; } = "success";

        [JsonProperty("data")]
        public string? Data { get; set; }

        [JsonProperty("error")]
        public string? Error { get; set; }

        /// <summary>附带的多模态内容（图片等）。</summary>
        [JsonIgnore]
        public List<ContentAttachment>? Attachments { get; set; }

        [JsonIgnore]
        public bool IsSuccess => Status == "success";
    }

    /// <summary>
    /// 工具结果附件（图片等多模态内容）。
    /// </summary>
    public class ContentAttachment
    {
        public string Type { get; set; } = "image";
        public string? Base64Data { get; set; }
        public string? MediaType { get; set; }
        public string? FilePath { get; set; }
    }
}
