using System;

namespace AgentCoreProcessor.Adapter
{
    public class IncomingMessage
    {
        public required string Platform { get; set; }
        public required string PlatformUserId { get; set; }
        public required string ChannelId { get; set; }
        public required string Content { get; set; }
        public string? ReplyTo { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;

        /// <summary>是否为私聊消息（控制台、文件流、私信等）。</summary>
        public bool IsPrivate { get; set; } = false;

        /// <summary>是否被 @（适配器层按平台特性填充）。</summary>
        public bool IsMentioned { get; set; } = false;
    }
}
