using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Adapter
{
    public class OutgoingMessage
    {
        public required string ChannelId { get; set; }
        public required string Content { get; set; }
        public string? ReplyTo { get; set; }

        /// <summary>需要 @ 的平台用户ID列表。</summary>
        public List<string>? Mentions { get; set; }

        /// <summary>消息附件（图片等，后续扩展用）。</summary>
        public List<MessageAttachment>? Attachments { get; set; }

        /// <summary>有序消息段（文本/图片/@/回复 交错排列）。适配器优先消费此字段。</summary>
        public List<MessageSegment>? Segments { get; set; }
    }
}
