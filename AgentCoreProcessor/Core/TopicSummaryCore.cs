using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 话题摘要生成器。根据对话内容生成/更新简短摘要，用于语义话题分类。
    /// </summary>
    internal class TopicSummaryCore : CoreBase
    {
        /// <summary>
        /// 根据话题名和最近消息生成初始摘要。
        /// </summary>
        public async Task<string> GenerateSummaryAsync(string topicName, List<string> recentMessages)
        {
            ResetProcessor();
            var sb = new StringBuilder();
            sb.AppendLine($"话题名称：{topicName}");
            sb.AppendLine("对话内容：");
            foreach (var msg in recentMessages)
                sb.AppendLine($"- {msg}");
            return await GenerateOnceAsync(sb.ToString());
        }

        /// <summary>
        /// 基于现有摘要和新消息更新摘要。
        /// </summary>
        public async Task<string> UpdateSummaryAsync(string currentSummary, List<string> newMessages)
        {
            ResetProcessor();
            var sb = new StringBuilder();
            sb.AppendLine($"当前摘要：{currentSummary}");
            sb.AppendLine("新增对话：");
            foreach (var msg in newMessages)
                sb.AppendLine($"- {msg}");
            sb.AppendLine("请根据新增对话更新摘要。");
            return await GenerateOnceAsync(sb.ToString());
        }
    }
}
