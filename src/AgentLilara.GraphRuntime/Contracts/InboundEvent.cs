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
    /// <summary>
    /// 兜底深度：0 = 正常事件；1 = 由一次失败派生出的错误事件。
    /// 错误事件只允许再走一跳兜底图，因此 <see cref="Depth"/> ≥ 1 不再派生新的错误事件。
    /// </summary>
    public int Depth { get; init; }

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

    /// <summary>
    /// 一条运行时失败事件：某次动作没能完成（如投递被拒、会话创建失败）。
    /// <para>失败是信息而不是异常：它作为新的一拍进入错误图，由电路决定怎么补偿。</para>
    /// </summary>
    public static InboundEvent RuntimeError(
        string channelKey,
        string reason,
        string? dedupeKey = null,
        int depth = 1,
        DateTimeOffset? at = null)
    {
        var dedupe = dedupeKey ?? $"error:{channelKey}:{reason.GetHashCode():x}";
        return new InboundEvent(
            $"error:{dedupe}",
            EventKind.RuntimeError,
            AdapterId: "graph",
            ChannelKey: channelKey,
            ChannelKind: ChannelKind.Unknown,
            SenderId: null,
            Text: reason,
            MentionsBot: false,
            IsReplyToBot: false,
            ReceivedAt: at ?? DateTimeOffset.UtcNow,
            DedupeKey: dedupe)
        {
            Depth = depth,
        };
    }
}
