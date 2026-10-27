using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.PluginSDK.Sources;

namespace AgentCoreProcessor.Engine.Source;

/// <summary>
/// 插件事件形状 → 宿主事件形状的唯一翻译点。
/// <para>为什么单独立一个类：信源契约活在 PluginSDK（net8），宿主事件形状在 GraphRuntime（net10），
/// 两者之间必然要有一层翻译。把它埋在 <see cref="SourceManager"/> 里的话，
/// "登记 / 启停 / 健康汇总 / 翻译"四件事挤在一个类，而后续 webhook、定时、文件变化
/// 都要走同一套翻译——那时就没有一个明确的地方可以只测翻译本身。</para>
/// </summary>
internal static class SourceEventTranslator
{
    /// <summary>
    /// 翻译成宿主的外部事件。
    /// <para>外部事件**没有频道**：<c>ChannelKey</c> 留空，因此它不会被按频道挂载的图看见，
    /// 投递目标由订阅记录决定。</para>
    /// </summary>
    internal static InboundEvent ToInbound(SourceEvent @event)
    {
        var dedupe = string.IsNullOrWhiteSpace(@event.DedupeKey)
            ? $"{@event.SourceId}:{@event.SenderId}:{@event.Text?.GetHashCode():x}"
            : @event.DedupeKey;

        // 归一成大小写不敏感的字典：插件可能用默认比较器构造，而图上的 event.ext 键名是约定，
        // 不该因为插件写成 "Subject" 还是 "subject" 就查不到。
        Dictionary<string, string>? ext = null;
        if (@event.Ext is { Count: > 0 })
        {
            ext = new Dictionary<string, string>(@event.Ext, StringComparer.OrdinalIgnoreCase);
        }

        return InboundEvent.External(
            sourceId: string.IsNullOrWhiteSpace(@event.SourceId) ? "unknown" : @event.SourceId,
            text: @event.Text ?? string.Empty,
            senderId: @event.SenderId,
            ext: ext,
            dedupeKey: dedupe,
            at: @event.ReceivedAt);
    }
}
