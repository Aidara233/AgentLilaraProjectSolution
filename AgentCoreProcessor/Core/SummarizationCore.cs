using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 摘要 Core。用于压缩系统循环上下文。
    /// </summary>
    internal class SummarizationCore : CoreBase
    {
        public SummarizationCore() : base("SummarizationCore")
        {
            ApplyExtraMessages();
        }

        /// <summary>
        /// 压缩上下文。保留关键信息，丢弃冗余细节。
        /// </summary>
        public async Task<string> SummarizeContextAsync(List<Message> messages, string? existingSummary = null)
        {
            var prompt = LoadPromptTemplate();

            var vars = new Dictionary<string, string>();
            vars["EXISTING_SUMMARY"] = !string.IsNullOrEmpty(existingSummary)
                ? "**现有摘要**：\n" + existingSummary + "\n"
                : "";

            var histSb = new StringBuilder();
            foreach (var msg in messages)
                histSb.AppendLine($"[{msg.Role}] {msg.Content}");
            vars["CONVERSATION_HISTORY"] = histSb.ToString();

            var fullPrompt = PromptLoader.ApplyVariables(prompt, vars);
            return await GenerateOnceAsync(fullPrompt);
        }

        private static string LoadPromptTemplate()
        {
            var coreDir = PathConfig.CoreConfigPath;
            var templatesDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates"));
            return PromptLoader.Load("SummarizationPrompt.txt", coreDir, templatesDir)
                   ?? "你是一个上下文压缩助手。将对话历史压缩成简洁摘要，保留关键信息，丢弃冗余细节。\n\n{{EXISTING_SUMMARY}}**需要压缩的对话历史**：\n{{CONVERSATION_HISTORY}}\n\n请输出压缩后的摘要：";
        }
    }
}
