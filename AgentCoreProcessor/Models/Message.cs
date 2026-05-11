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

        /// <summary>图片 base64 数据（type=image 时使用）。设置后优先于 ImagePath。</summary>
        public string? ImageBase64 { get; set; }

        /// <summary>图片 MIME 类型（与 ImageBase64 配合使用，如 "image/png"）。</summary>
        public string? MediaType { get; set; }

        // ── 工厂方法 ──

        public static ContentPart FromText(string text)
            => new() { Type = "text", Text = text };

        public static ContentPart FromImagePath(string path)
            => new() { Type = "image", ImagePath = path };

        public static ContentPart FromImageBase64(string base64, string mediaType)
            => new() { Type = "image", ImageBase64 = base64, MediaType = mediaType };
    }
}