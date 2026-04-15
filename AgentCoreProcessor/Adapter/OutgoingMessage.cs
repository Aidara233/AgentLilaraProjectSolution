using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Adapter
{
    public class OutgoingMessage
    {
        public required string ChannelId { get; set; }
        public required string Content { get; set; }
        public string? ReplyTo { get; set; }

        /// <summary>消息附件（图片等，后续扩展用）。</summary>
        public List<MessageAttachment>? Attachments { get; set; }
    }
}
