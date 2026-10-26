namespace AgentLilara.PluginSDK.Sources;

/// <summary>
/// 一条外部信源事件。**信源与宿主之间的唯一形状**。
/// <para>为什么不是宿主的 <c>InboundEvent</c>：那是 net10 程序集里的类型，插件（net8）够不着。
/// 这一层刻意只有"信源能产生的东西"，不带任何频道/会话概念——外部事件本来就没有频道，
/// 投递目标由订阅记录决定。</para>
/// </summary>
public sealed class SourceEvent
{
    /// <summary>信源标识（`mail` / `webhook`…）。粗筛键，框架按它找候选订阅。</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>正文（邮件即正文纯文本）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>发件人（可选）。</summary>
    public string? SenderId { get; set; }

    /// <summary>信源特有的结构化字段（如 subject / uid / folder）。缺失的键在图上表现为 unknown。</summary>
    public Dictionary<string, string> Ext { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>去重键。同一封信重复投递必须给同一个值（如 uidvalidity:uid）。</summary>
    public string DedupeKey { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
