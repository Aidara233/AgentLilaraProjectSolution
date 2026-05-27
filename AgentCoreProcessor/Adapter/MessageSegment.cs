using System.Collections.Generic;

namespace AgentCoreProcessor.Adapter
{
    public enum SegmentType
    {
        Text,
        Image,
        At,
        Reply
    }

    public class MessageSegment
    {
        public SegmentType Type { get; init; }
        public string? Text { get; init; }
        public string? ImagePath { get; init; }
        public string? AtPlatformId { get; init; }
        public string? ReplyId { get; init; }
    }
}
