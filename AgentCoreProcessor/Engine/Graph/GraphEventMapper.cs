using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Engine.Messaging;
using AgentLilara.GraphRuntime.Contracts;

namespace AgentCoreProcessor.Engine.Graph;

internal static class GraphEventMapper
{
    /// <summary>适配器入站消息 → 图事件。</summary>
    public static InboundEvent ToEvent(IncomingMessage msg, int channelId)
    {
        return InboundEvent.Message(
            adapterId: msg.AdapterId ?? msg.Platform,
            channelKey: channelId.ToString(),
            text: msg.Content ?? string.Empty,
            channelKind: msg.IsPrivate ? ChannelKind.Direct : ChannelKind.Group,
            senderId: msg.PlatformUserId,
            mentionsBot: msg.IsMentioned,
            at: new DateTimeOffset(msg.Time),
            dedupeKey: msg.PlatformMessageId ?? $"{msg.Platform}:{msg.ChannelId}:{msg.PlatformUserId}:{msg.Time:O}");
    }

    /// <summary>WebUI/引擎内部入站消息 → 图事件。</summary>
    public static InboundEvent ToEvent(EngineIncomingMessage msg)
    {
        return InboundEvent.Message(
            adapterId: "alp",
            channelKey: msg.ChannelId.ToString(),
            text: msg.Content ?? string.Empty,
            channelKind: ChannelKind.Group,
            senderId: msg.SenderUserId.ToString(),
            mentionsBot: string.Equals(msg.Tag, "mention", StringComparison.OrdinalIgnoreCase),
            at: msg.Timestamp == default ? DateTimeOffset.UtcNow : new DateTimeOffset(msg.Timestamp),
            dedupeKey: msg.MessageId);
    }
}
