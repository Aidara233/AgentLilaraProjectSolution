namespace AgentLilara.GraphRuntime.Contracts;

/// <summary>
/// 事件种类。**按"产生者主体"划分，不按内容变体划分**（D60）：
/// 同一主体的同刻属性是字段（群/私聊、是否@），不同主体的行为是不同事件（收消息 ≠ 入库）。
/// </summary>
public enum EventKind
{
    MessageReceived,
    ClockTick,

    // 归档保留：骨架落地后由 store / session / adapter / governor 发出
    SessionFinished,
    SessionFailed,
    AdapterStateChanged,
    RuntimeError,
}

/// <summary>频道形态。同一事件上的字段，不拆事件。</summary>
public enum ChannelKind
{
    Unknown,
    Group,
    Direct,
}

/// <summary>
/// 入站事件：图的唯一输入（"信号端是事件"）。
/// 加一个新信源（邮件、webhook）不需要新增事件种类，只需换 <see cref="AdapterId"/>。
/// </summary>
public sealed record InboundEvent(
    string EventId,
    EventKind Kind,
    string AdapterId,
    string ChannelKey,
    ChannelKind ChannelKind,
    string? SenderId,
    string Text,
    bool MentionsBot,
    bool IsReplyToBot,
    DateTimeOffset ReceivedAt,
    string DedupeKey)
{
    /// <summary>一条频道消息。</summary>
    public static InboundEvent Message(
        string adapterId,
        string channelKey,
        string text,
        ChannelKind channelKind = ChannelKind.Group,
        string? senderId = null,
        bool mentionsBot = false,
        bool isReplyToBot = false,
        string? dedupeKey = null,
        DateTimeOffset? at = null,
        string? eventId = null)
    {
        var dedupe = dedupeKey ?? $"{adapterId}:{channelKey}:{senderId}:{text.GetHashCode():x}";
        return new InboundEvent(
            eventId ?? dedupe,
            EventKind.MessageReceived,
            adapterId,
            channelKey,
            channelKind,
            senderId,
            text,
            mentionsBot,
            isReplyToBot,
            at ?? DateTimeOffset.UtcNow,
            dedupe);
    }

    /// <summary>一个时钟沿（动态图专用；归档保留）。</summary>
    public static InboundEvent Clock(long seq, DateTimeOffset? at = null) => new(
        $"clock:{seq}",
        EventKind.ClockTick,
        AdapterId: "clock",
        ChannelKey: string.Empty,
        ChannelKind: ChannelKind.Unknown,
        SenderId: null,
        Text: string.Empty,
        MentionsBot: false,
        IsReplyToBot: false,
        ReceivedAt: at ?? DateTimeOffset.UtcNow,
        DedupeKey: $"clock:{seq}");
}
