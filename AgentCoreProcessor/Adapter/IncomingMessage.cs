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
    }
}
