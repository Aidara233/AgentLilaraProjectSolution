using AgentCoreProcessor.Engine.Graph;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host;

/// <summary>
/// <see cref="IWatchAccess"/> 的实现：把模型侧的"盯着"翻译成一条运行时订阅。
/// <para>⚠️ 模型只能选预制图（`preset.watch`）并填参数，不能写图本身（D62）。</para>
/// <para>⚠️ 归属校验：只能取消**本频道**的订阅，否则一个被注入的会话就能拆掉别的频道的监视。</para>
/// </summary>
internal sealed class WatchAccessImpl : IWatchAccess
{
    /// <summary>订阅统一用这张预制图做判定。</summary>
    private const string WatchClassId = "preset.watch";

    private readonly GraphManager? _graphs;

    public WatchAccessImpl(GraphManager? graphs) => _graphs = graphs;

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

        var subscription = new GraphSubscription
        {
            ChannelId = channelId,
            ClassId = WatchClassId,
            Source = request.Source.Trim(),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            MaxFires = request.MaxFires <= 0 ? 1 : request.MaxFires,
            ExpiresAt = request.TtlMinutes > 0
                ? DateTimeOffset.UtcNow.AddMinutes(request.TtlMinutes)
                : null,
        };

        if (!string.IsNullOrWhiteSpace(request.From))
        {
            subscription.Params["from"] = request.From.Trim();
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
}
