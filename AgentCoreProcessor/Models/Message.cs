using System.Collections.Generic;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Models
{
    public class Message
    {
        [JsonProperty("role")]
        public string Role { get; set; } = "user"; // system, user, assistant

        [JsonProperty("content")]
        public string Content { get; set; } = "";

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string? Name { get; set; }

        /// <summary>多模态内容块。非空时优先于 Content 使用。</summary>
        [JsonIgnore]
        public List<ContentPart>? ContentParts { get; set; }
    }

    public class ContentPart
    {
        /// <summary>内容类型：text | image</summary>
        public string Type { get; set; } = "text";

        /// <summary>文本内容（type=text 时使用）。</summary>
        public string? Text { get; set; }

        /// <summary>图片本地路径（type=image 时使用），由 ModelClient 转为 base64。</summary>
        public string? ImagePath { get; set; }
    }
}