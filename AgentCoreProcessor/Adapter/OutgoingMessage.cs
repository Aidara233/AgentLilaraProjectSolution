using System;

namespace AgentCoreProcessor.Adapter
{
    public class OutgoingMessage
    {
        public required string ChannelId { get; set; }
        public required string Content { get; set; }
        public string? ReplyTo { get; set; }
    }
}
