using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Engine.Vision
{
    internal static class VisionOutputParser
    {
        internal static (string Classification, string Description) Parse(string output)
        {
            output = output.Trim();
            if (output.Length == 0)
                return ("unknown", "[视觉模型返回空内容]");

            // 1. 尝试直接解析 JSON
            var result = TryParseJson(output);
            if (result != null) return result.Value;

            // 2. 提取 ```json ... ``` 代码块
            var jsonBlock = Regex.Match(output, @"```json\s*\n?(.*?)\n?```", RegexOptions.Singleline);
            if (jsonBlock.Success)
            {
                result = TryParseJson(jsonBlock.Groups[1].Value.Trim());
                if (result != null) return result.Value;
            }

            // 3. 提取 ``` ... ``` 代码块（无语言标注）
            var codeBlock = Regex.Match(output, @"```\s*\n?(.*?)\n?```", RegexOptions.Singleline);
            if (codeBlock.Success)
            {
                result = TryParseJson(codeBlock.Groups[1].Value.Trim());
                if (result != null) return result.Value;
            }

            // 4. 兜底：整段文本当描述
            return ("unknown", output);
        }

        private static (string, string)? TryParseJson(string text)
        {
            try
            {
                var obj = JObject.Parse(text);
                var classification = obj["classification"]?.ToString()?.Trim() ?? "";
                var description = obj["description"]?.ToString()?.Trim() ?? "";
                classification = NormalizeClassification(classification);
                // 有分类就接受，description 可为空（后续 Phase 会补充）
                if (!string.IsNullOrEmpty(classification) || !string.IsNullOrEmpty(description))
                    return (classification, description);
            }
            catch { }
            return null;
        }

        private static string NormalizeClassification(string raw)
        {
            return raw switch
            {
                "info-screenshot" => "info-screenshot",
                "info-photo" => "info-photo",
                "share-photo" => "share-photo",
                "share-art" => "share-art",
                "meme" => "meme",
                "sticker" => "sticker",
                "unknown" => "unknown",
                _ => "unknown"
            };
        }

    }
}
