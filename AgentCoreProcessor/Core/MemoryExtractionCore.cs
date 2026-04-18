using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 提取结果：事实或反馈。
    /// </summary>
    internal class ExtractionResult
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "fact"; // fact | feedback

        [JsonProperty("content")]
        public string Content { get; set; } = "";

        [JsonProperty("confidence")]
        public string Confidence { get; set; } = "high"; // high | low

        [JsonProperty("sentiment")]
        public string? Sentiment { get; set; } // positive | negative (feedback only)

        [JsonProperty("about")]
        public string? About { get; set; } // 关于谁（对话中出现的名字）

        [JsonProperty("correction")]
        public string? Correction { get; set; } // negative feedback only
    }

    /// <summary>
    /// 记忆提取核心。从对话中提取事实和反馈，带置信度标记。
    /// </summary>
    internal class MemoryExtractionCore : CoreBase
    {
        protected override bool UsePersona => false;

        /// <summary>
        /// 从对话历史中提取事实和反馈。
        /// </summary>
        public async Task<List<ExtractionResult>> ExtractAsync(List<string> conversationLines)
        {
            ResetProcessor();
            var sb = new StringBuilder();
            sb.AppendLine("以下是一段对话：");
            foreach (var line in conversationLines)
                sb.AppendLine(line);
            var result = await GenerateOnceAsync(sb.ToString());
            return ParseResults(result);
        }

        /// <summary>解析模型输出。优先 JSON，fallback 为旧的按行解析。</summary>
        private static List<ExtractionResult> ParseResults(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return new();

            var trimmed = output.Trim();
            if (trimmed == "无" || trimmed == "无。" || trimmed == "[]") return new();

            // 尝试 JSON 解析
            try
            {
                var results = JsonConvert.DeserializeObject<List<ExtractionResult>>(trimmed);
                if (results != null && results.Count > 0)
                    return results.Where(r => !string.IsNullOrWhiteSpace(r.Content)).ToList();
            }
            catch { }

            // fallback：按行解析，全部标为 high confidence fact
            return trimmed
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimStart('-', '·', ' ', '\t'))
                .Where(line => !string.IsNullOrWhiteSpace(line) && line != "无" && line != "无。")
                .Select(line => new ExtractionResult { Content = line })
                .ToList();
        }
    }
}
