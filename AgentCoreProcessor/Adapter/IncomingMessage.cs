using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Adapter
{
    public class IncomingMessage
    {
        public required string Platform { get; set; }
        public required string PlatformUserId { get; set; }
        public required string ChannelId { get; set; }
        public required string Content { get; set; }

        /// <summary>发言人显示名（群名片优先，昵称兜底）。适配器层填充。</summary>
        public string? DisplayName { get; set; }
        /// <summary>发言人平台昵称（原始昵称，不随群变化）。</summary>
        public string? Nickname { get; set; }

        public string? ReplyTo { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;

        /// <summary>是否为私聊消息（控制台、文件流、私信等）。</summary>
        public bool IsPrivate { get; set; } = false;

        /// <summary>是否被 @（适配器层按平台特性填充）。</summary>
        public bool IsMentioned { get; set; } = false;

        /// <summary>被引用消息的文本内容（适配器层填充）。</summary>
        public string? QuotedContent { get; set; }

        /// <summary>平台侧消息ID（用于数据库关联）。</summary>
        public string? PlatformMessageId { get; set; }

        /// <summary>消息附件（图片、音频等）。</summary>
        public List<MessageAttachment>? Attachments { get; set; }

        /// <summary>被@的平台用户ID列表（包括非bot用户）。</summary>
        public List<string>? MentionedPlatformIds { get; set; }
    }
}
