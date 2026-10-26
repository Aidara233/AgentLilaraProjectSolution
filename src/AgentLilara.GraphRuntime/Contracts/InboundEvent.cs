namespace AgentLilara.GraphRuntime.Contracts;

/// <summary>
/// 事件种类。**按"产生者主体"划分，不按内容变体划分**（D60）：
/// 同一主体的同刻属性是字段（群/私聊、是否@），不同主体的行为是不同事件（收消息 ≠ 入库）。
/// </summary>
public enum EventKind
{
    MessageReceived,
    ClockTick,

    /// <summary>
    /// 外部信源事件（邮件、webhook…）。**不是聊天消息**：没有频道，不参与按频道挂载的图，
    /// 也不进入会话的消息管线。加一个外部信源只换 <see cref="InboundEvent.AdapterId"/>（信源标识），
    /// 载荷放进 <see cref="InboundEvent.Ext"/>。
    /// </summary>
    External,

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

    /// <summary>
    /// 外部信源的结构化载荷（`ext.*` 命名空间，如邮件的 subject / from / thread）。
    /// <para>⚠️ 缺失的键在图里是 <c>unknown</c> 而不是空串——"没有这个字段"与"字段为空"必须能分开，
    /// 否则 <c>compare.eq</c> 会把两者当成同一件事。</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Ext { get; init; } = EmptyExt;

    /// <summary>
    /// 产生这条事件的信源标识。聊天消息里它等于适配器 id；外部事件里它是信源 id（`mail` / `webhook`…）。
    /// <para>字段本身沿用 <see cref="AdapterId"/> 以免破坏既有调用点，语义以本属性为准。</para>
    /// </summary>
    public string SourceId => AdapterId;

    /// <summary>空载荷袋（单例，避免每条消息都分配一个字典）。</summary>
    public static readonly IReadOnlyDictionary<string, string> EmptyExt =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
    /// 一条外部信源事件（邮件、webhook、文件变化…）。
    /// <para>⚠️ 外部事件**没有频道**：<see cref="InboundEvent.ChannelKey"/> 留空，因此不会被
    /// 按频道挂载的图看见（`on.message` 也不认它）。投递目标由订阅记录决定，不由事件决定。</para>
    /// <para>信源特有的字段放 <paramref name="ext"/>；正文放 <paramref name="text"/>（邮件即正文）。</para>
    /// </summary>
    public static InboundEvent External(
        string sourceId,
        string text,
        string? senderId = null,
        IReadOnlyDictionary<string, string>? ext = null,
        string? dedupeKey = null,
        DateTimeOffset? at = null,
        string? eventId = null)
    {
        var dedupe = dedupeKey ?? $"{sourceId}:{senderId}:{text.GetHashCode():x}";
        return new InboundEvent(
            eventId ?? dedupe,
            EventKind.External,
            AdapterId: sourceId,
            ChannelKey: string.Empty,
            ChannelKind: ChannelKind.Unknown,
            SenderId: senderId,
            Text: text,
            MentionsBot: false,
            IsReplyToBot: false,
            ReceivedAt: at ?? DateTimeOffset.UtcNow,
            DedupeKey: dedupe)
        {
            Ext = ext ?? EmptyExt,
        };
    }

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
