using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 记忆提取核心。从对话中提取值得长期记住的事实，写入临时记忆。
    /// </summary>
    internal class MemoryExtractionCore : CoreBase
    {
        protected override bool UsePersona => false;

        /// <summary>
        /// 从对话历史中提取值得记住的事实。
        /// </summary>
        public async Task<List<string>> ExtractAsync(List<string> conversationLines)
        {
            ResetProcessor();
            var sb = new StringBuilder();
            sb.AppendLine("以下是一段对话：");
            foreach (var line in conversationLines)
                sb.AppendLine(line);
            var result = await GenerateOnceAsync(sb.ToString());
            return ParseFacts(result);
        }

        /// <summary>解析模型输出为事实列表。每行一条，"无"表示没有值得记录的。</summary>
        private static List<string> ParseFacts(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return new();

            var trimmed = output.Trim();
            if (trimmed == "无" || trimmed == "无。") return new();

            return trimmed
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimStart('-', '·', ' ', '\t'))
                .Where(line => !string.IsNullOrWhiteSpace(line) && line != "无" && line != "无。")
                .ToList();
        }
    }
}
