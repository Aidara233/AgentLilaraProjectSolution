using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 话题候选项，用于分类时提供已有话题信息。
    /// </summary>
    internal class TopicCandidate
    {
        public int TopicId { get; set; }
        public string Summary { get; set; } = "";
    }

    /// <summary>
    /// 对话段分类结果。
    /// </summary>
    internal class MessageSegment
    {
        public List<int> MessageIds { get; set; } = new();
        /// <summary>已有话题ID，-1=新话题，-2=闲聊。</summary>
        public int TopicId { get; set; }
        public string SuggestedName { get; set; } = "";
    }

    /// <summary>
    /// 话题分段分类器（做梦用）。对完整对话段进行批量归类，
    /// 区别于旧的逐条 TopicClassificationCore。
    /// </summary>
    internal class TopicSegmentationCore : CoreBase
    {
        protected override bool UsePersona => false;

        /// <summary>
        /// 对一个对话段进行分类：归入已有话题、新建话题、或闲聊。
        /// </summary>
        public async Task<MessageSegment> ClassifySegmentAsync(
            List<UserMessage> segment, List<TopicCandidate> existingTopics)
        {
            ResetProcessor();

            var sb = new StringBuilder();

            // 已有话题候选
            if (existingTopics.Count > 0)
            {
                sb.AppendLine("已有话题：");
                foreach (var t in existingTopics)
                    sb.AppendLine($"[话题{t.TopicId}] {t.Summary}");
                sb.AppendLine();
            }

            // 对话段内容
            sb.AppendLine("对话段：");
            foreach (var msg in segment)
            {
                var name = msg.IsFromBot ? "Lilara"
                         : !string.IsNullOrEmpty(msg.SenderName) ? msg.SenderName
                         : "用户";
                sb.AppendLine($"{name}: {msg.Content}");
            }

            var result = await GenerateOnceAsync(sb.ToString());
            result = result.Trim();

            var ids = segment.Select(m => m.Id).ToList();

            // 解析结果
            if (result.Equals("chat", System.StringComparison.OrdinalIgnoreCase))
                return new MessageSegment { MessageIds = ids, TopicId = -2 };

            // "new:话题名" 格式
            if (result.StartsWith("new:", System.StringComparison.OrdinalIgnoreCase))
            {
                var name = result[4..].Trim();
                if (string.IsNullOrEmpty(name))
                    name = segment[0].Content.Length > 20
                        ? segment[0].Content[..20] + "..."
                        : segment[0].Content;
                return new MessageSegment { MessageIds = ids, TopicId = -1, SuggestedName = name };
            }

            if (result.Equals("new", System.StringComparison.OrdinalIgnoreCase))
            {
                var name = segment[0].Content.Length > 20
                    ? segment[0].Content[..20] + "..."
                    : segment[0].Content;
                return new MessageSegment { MessageIds = ids, TopicId = -1, SuggestedName = name };
            }

            if (int.TryParse(result, out var topicId))
                return new MessageSegment { MessageIds = ids, TopicId = topicId };

            // 无法解析 → 闲聊
            return new MessageSegment { MessageIds = ids, TopicId = -2 };
        }
    }
}
