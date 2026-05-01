using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 摘要 Core。用于压缩系统循环上下文。
    /// UsePersona=false，纯工具性。
    /// </summary>
    internal class SummarizationCore : CoreBase
    {
        protected override bool UsePersona => false;

        public SummarizationCore() : base("SummarizationCore")
        {
            ApplyExtraMessages();
        }

        /// <summary>
        /// 压缩上下文。保留关键信息，丢弃冗余细节。
        /// </summary>
        public async Task<string> SummarizeContextAsync(List<Message> messages, string? existingSummary = null)
        {
            var prompt = new StringBuilder();
            prompt.AppendLine("你是一个上下文压缩助手。你的任务是将对话历史压缩成简洁的摘要，保留关键信息。");
            prompt.AppendLine();
            prompt.AppendLine("**压缩原则**：");
            prompt.AppendLine("1. 保留所有重要决策、任务状态、待办事项");
            prompt.AppendLine("2. 保留便签板内容、任务列表");
            prompt.AppendLine("3. 保留最近的工具调用结果（最近 5 轮）");
            prompt.AppendLine("4. 丢弃冗余的中间过程、重复信息");
            prompt.AppendLine("5. 用简洁的语言概括，不要逐条罗列");
            prompt.AppendLine();

            if (!string.IsNullOrEmpty(existingSummary))
            {
                prompt.AppendLine("**现有摘要**：");
                prompt.AppendLine(existingSummary);
                prompt.AppendLine();
            }

            prompt.AppendLine("**需要压缩的对话历史**：");
            foreach (var msg in messages)
            {
                prompt.AppendLine($"[{msg.Role}] {msg.Content}");
            }

            prompt.AppendLine();
            prompt.AppendLine("请输出压缩后的摘要（纯文本，不要 markdown 格式）：");

            return await GenerateOnceAsync(prompt.ToString());
        }
    }
}
