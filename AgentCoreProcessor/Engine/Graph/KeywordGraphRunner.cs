using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Engine.Messaging;
using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;

namespace AgentCoreProcessor.Engine.Graph;

/// <summary>
/// 入站关键词图：把适配器/WebUI 进来的真实消息送进 preset.keyword-test。
/// <para>默认 shadow：只记录意图。只有 active 才把意图交给骨架执行。</para>
/// <para>⚠️ 本类不写库、不发消息、不碰适配器；它唯一的出口是调用方给的 dispatch 回调。</para>
/// </summary>
internal sealed class KeywordGraphRunner
{
    private readonly GraphInstance _graph;
    private readonly bool _active;
    private readonly IReadOnlyDictionary<string, string> _mountParameters;

    private KeywordGraphRunner(
        GraphInstance graph,
        bool active,
        IReadOnlyDictionary<string, string>? mountParameters = null)
    {
        _graph = graph;
        _active = active;
        _mountParameters = mountParameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsActive => _active;

    /// <summary>当前实例（诊断/测试用：读变量、确认实例隔离）。</summary>
    internal GraphInstance Instance => _graph;

    public static KeywordGraphRunner? TryCreate(bool active) =>
        TryCreateFrom("preset.keyword-agent.json", active);

    /// <summary>用已加载的图实例构造（挂载表按频道选图时使用）。</summary>
    public static KeywordGraphRunner ForInstance(
        GraphInstance graph,
        bool active,
        IReadOnlyDictionary<string, string>? mountParameters = null) =>
        new(graph, active, mountParameters);


    /// <summary>按图文件名加载（测试与不同预制图共用）。</summary>
    public static KeywordGraphRunner? TryCreateFrom(string fileName, bool active)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Graphs", fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            return new KeywordGraphRunner(
                new GraphInstance(GraphLoader.Load(File.ReadAllText(path))),
                active);
        }
        catch (Exception ex)
        {
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.keyword.load_failed", new { fileName, error = ex.Message });
            return null;
        }
    }

    /// <summary>
    /// 跑一次图，把**本次激活**产出的意图交回调用方。
    /// <para>图没有返回值：它只产出一组意图（可能是空集），执行与否归骨架；跑完即待命。</para>
    /// <para>返回值取自本次求解的局部结果，不读实例上的 <see cref="GraphInstance.LastIntents"/>，
    /// 因此不依赖"调用后还没被别的拍覆盖"这种时序前提。</para>
    /// </summary>
    public async Task<IReadOnlyList<ActionIntent>> EvaluateAsync(InboundEvent @event, string traceId)
    {
        var intents = await RunCoreAsync(@event, traceId);

        Logging.Signal.Debug(Logging.LogGroup.Engine, "graph.keyword.evaluated",
            new { traceId, active = _active, intents = intents.Count });

        return intents;
    }

    private static async Task DispatchAsync(ActionIntent intent, string traceId, Func<ActionIntent, Task> dispatch)
    {
        try
        {
            await dispatch(intent);
        }
        catch (Exception ex)
        {
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.keyword.dispatch_failed",
                new { traceId, intent.Kind, intent.Target, error = ex.Message });
        }
    }

    /// <summary>跑一次图并把意图全部交给调用方回调（测试与小工具用）。</summary>
    public async Task RunAsync(InboundEvent @event, string traceId, Func<ActionIntent, Task> dispatch)
    {
        var intents = await EvaluateAsync(@event, traceId);

        foreach (var intent in intents)
        {
            Logging.Signal.Debug(Logging.LogGroup.Engine, "graph.keyword.intent",
                new { traceId, intent.Kind, intent.Target, active = _active });

            if (!_active)
            {
                continue;
            }

            await DispatchAsync(intent, traceId, dispatch);
        }
    }

    private async Task<IReadOnlyList<ActionIntent>> RunCoreAsync(InboundEvent @event, string traceId)
    {
        // 事件种类由激活源自己筛（on.message / on.error）：这里不做二次过滤，
        // 否则同一张图无法复用到别的触发源上。
        SolveResult result;
        try
        {
            var context = new EvalContext($"graph-keyword:{traceId}", DateTime.UtcNow.Ticks, @event)
            {
                MountParameters = _mountParameters
            };
            result = _graph.Run(context);
        }
        catch (Exception ex)
        {
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.keyword.threw", new { traceId, error = ex.Message });
            return Array.Empty<ActionIntent>();
        }

        if (!result.Success)
        {
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.keyword.solve_invalid",
                new { traceId, result.Unresolved });
            return Array.Empty<ActionIntent>();
        }

        return _graph.LastIntents;
    }

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
