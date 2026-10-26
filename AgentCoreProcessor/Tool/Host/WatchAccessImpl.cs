using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Source;
using AgentLilara.PluginSDK.Services;
using AgentLilara.PluginSDK.Sources;

namespace AgentCoreProcessor.Tool.Host;

/// <summary>
/// <see cref="IWatchAccess"/> 的实现：把模型侧的"盯着"翻译成一条运行时订阅。
/// <para>⚠️ 模型只能选预制图（`preset.watch`）并填参数，不能写图本身（D62）。</para>
/// <para>⚠️ 归属校验：只能取消**本频道**的订阅，否则一个被注入的会话就能拆掉别的频道的监视。</para>
/// <para>⚠️ 创建时校验信源与字段名：写错 `source` 或 `from` 原本会被静默受理，
/// 然后永远匹配不上——失败表现是"什么都不发生"，是最难排查的一类问题。</para>
/// </summary>
internal sealed class WatchAccessImpl : IWatchAccess
{
    /// <summary>订阅统一用这张预制图做判定。</summary>
    private const string WatchClassId = "preset.watch";

    /// <summary>这张图唯一读取的载荷键（与 `preset.watch` 的 `event.ext` 参数一致）。</summary>
    private const string FilterField = "from";

    private readonly GraphManager? _graphs;
    private readonly SourceManager? _sources;

    public WatchAccessImpl(GraphManager? graphs, SourceManager? sources = null)
    {
        _graphs = graphs;
        _sources = sources;
    }

    public WatchResult Create(int channelId, WatchRequest request)
    {
        if (_graphs is null)
        {
            return WatchResult.Fail("图系统未启用");
        }

        if (channelId <= 0)
        {
            return WatchResult.Fail("当前会话没有绑定频道，订阅需要一个宿主频道");
        }

        if (string.IsNullOrWhiteSpace(request.Source))
        {
            return WatchResult.Fail("source 必填（例如 mail）");
        }

        var sourceId = request.Source.Trim();

        // 信源必须真实存在：受理一个不存在信源的订阅，等于登记一条永远不会命中的规则。
        if (_sources is not null)
        {
            var source = ResolveSource(sourceId);
            if (source is null)
            {
                var available = AvailableSources();
                return WatchResult.Fail(available.Count == 0
                    ? $"没有已登记的信源，无法等待「{sourceId}」"
                    : $"没有信源「{sourceId}」。可用：{string.Join("、", available)}");
            }

            var unknown = UnknownFields(source, request);
            if (unknown.Count > 0)
            {
                return WatchResult.Fail(
                    $"信源「{source.Kind}」不提供字段 {string.Join("、", unknown)}" +
                    $"（支持：{string.Join("、", source.Fields)}）");
            }
        }

        var subscription = new GraphSubscription
        {
            ChannelId = channelId,
            ClassId = WatchClassId,
            Source = sourceId,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            MaxFires = request.MaxFires <= 0 ? 1 : request.MaxFires,
            ExpiresAt = request.TtlMinutes > 0
                ? DateTimeOffset.UtcNow.AddMinutes(request.TtlMinutes)
                : null,
        };

        if (!string.IsNullOrWhiteSpace(request.From))
        {
            subscription.Params[FilterField] = request.From.Trim();
        }

        var error = _graphs.AddSubscription(subscription);

        return error is null
            ? WatchResult.Ok(subscription.Id)
            : WatchResult.Fail(error);
    }

    public bool Cancel(int channelId, string watchId)
    {
        if (_graphs is null || channelId <= 0 || string.IsNullOrWhiteSpace(watchId))
        {
            return false;
        }

        var owned = _graphs.ListSubscriptions(channelId)
            .Any(subscription => string.Equals(subscription.Id, watchId, StringComparison.Ordinal));

        return owned && _graphs.RemoveSubscription(watchId, "cancelled-by-agent");
    }

    public IReadOnlyList<WatchInfo> List(int channelId)
    {
        if (_graphs is null || channelId <= 0)
        {
            return Array.Empty<WatchInfo>();
        }

        return _graphs.ListSubscriptions(channelId)
            .Select(subscription => new WatchInfo
            {
                Id = subscription.Id,
                Source = subscription.Source,
                Note = subscription.Note,
                MaxFires = subscription.MaxFires,
                FiredCount = subscription.FiredCount,
                ExpiresAt = subscription.ExpiresAt,
            })
            .ToList();
    }

    /// <summary>
    /// 可用信源及其字段，供工具描述与错误提示使用。
    /// <para>把它暴露出来，模型才知道该传什么，而不是靠猜。</para>
    /// </summary>
    public IReadOnlyList<SourceCapability> DescribeSources()
    {
        if (_sources is null)
        {
            return Array.Empty<SourceCapability>();
        }

        return _sources.All
            .Select(source => new SourceCapability
            {
                Kind = source.Kind,
                Fields = source.Fields,
            })
            .ToList();
    }

    /// <summary>按 Id 或 Kind 找信源——两者通常是同一个值，但插件可以起不同名字。</summary>
    private IEventSource? ResolveSource(string idOrKind)
    {
        if (_sources is null)
        {
            return null;
        }

        return _sources.Get(idOrKind)
               ?? _sources.All.FirstOrDefault(source =>
                   string.Equals(source.Kind, idOrKind, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<string> AvailableSources() =>
        _sources?.All
            .Select(source => source.Kind)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>
    /// 本次订阅要用到、但信源没声明的键。
    /// <para>信源不声明 <see cref="IEventSource.Fields"/>（空集）时不做校验——向后兼容老插件。</para>
    /// </summary>
    private static IReadOnlyList<string> UnknownFields(IEventSource source, WatchRequest request)
    {
        if (source.Fields.Count == 0)
        {
            return Array.Empty<string>();
        }

        var used = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.From))
        {
            used.Add(FilterField);
        }

        return used
            .Where(field => !source.Fields.Contains(field, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }
}
