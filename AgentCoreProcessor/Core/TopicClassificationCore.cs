using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 话题分类器（模型兜底层）。当向量匹配置信度不足时，由模型判断消息归属。
    /// </summary>
    internal class TopicClassificationCore : CoreBase
    {
        protected override bool UsePersona => false;
        /// <summary>
        /// 判断新消息属于哪个候选话题，或应新建话题。
        /// 返回话题ID，或 -1 表示新建话题。
        /// </summary>
        public async Task<int> ClassifyAsync(string newMessage, List<TopicCandidate> candidates)
        {
            ResetProcessor();
            var sb = new StringBuilder();
            sb.AppendLine("候选话题：");
            foreach (var c in candidates)
            {
                sb.AppendLine($"[话题{c.TopicId}] 摘要：{c.Summary}");
                if (c.RecentMessages.Count > 0)
                {
                    sb.AppendLine("  最近消息：");
                    foreach (var msg in c.RecentMessages)
                        sb.AppendLine($"    - {msg}");
                }
            }
            sb.AppendLine($"\n新消息：{newMessage}");

            var result = await GenerateOnceAsync(sb.ToString());
            result = result.Trim();

            if (result.Equals("new", System.StringComparison.OrdinalIgnoreCase))
                return -1;

            if (int.TryParse(result, out var topicId))
                return topicId;

            return -1; // 无法解析时默认新建
        }
    }

    internal class TopicCandidate
    {
        public int TopicId { get; set; }
        public string Summary { get; set; } = "";
        public List<string> RecentMessages { get; set; } = [];
    }
}
