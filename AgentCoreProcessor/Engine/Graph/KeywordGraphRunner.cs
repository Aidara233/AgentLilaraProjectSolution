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

    private KeywordGraphRunner(GraphInstance graph, bool active)
    {
        _graph = graph;
        _active = active;
    }

    public bool IsActive => _active;

    /// <summary>当前实例（诊断/测试用：读变量、确认实例隔离）。</summary>
    internal GraphInstance Instance => _graph;

    public static KeywordGraphRunner? TryCreate(bool active) =>
        TryCreateFrom("preset.keyword-agent.json", active);

    /// <summary>用已加载的图实例构造（挂载表按频道选图时使用）。</summary>
    public static KeywordGraphRunner ForInstance(GraphInstance graph, bool active) =>
        new(graph, active);

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
    /// 门控 + 派发：图是否认为这条消息应当进入会话；同时把 SendMessage 意图派发出去。
    /// <para>shadow 模式只记录结论；active 模式调用方据返回值决定是否继续会话激活。</para>
    /// </summary>
    public async Task<bool> DecideAsync(InboundEvent @event, string traceId, Func<ActionIntent, Task>? dispatch = null)
    {
        var intents = await RunCoreAsync(@event, traceId);
        var allow = false;

        foreach (var intent in intents)
        {
            if (intent.Kind is IntentKind.SessionPrompt or IntentKind.SessionRoute)
            {
                allow = true;
                if (intent.Kind == IntentKind.SessionRoute && _active && dispatch != null)
                    await DispatchAsync(intent, traceId, dispatch);
            }
            else if (intent.Kind == IntentKind.SendMessage)
            {
                allow = true;
                if (_active && dispatch != null)
                    await DispatchAsync(intent, traceId, dispatch);
            }
        }

        Logging.Signal.Debug(Logging.LogGroup.Engine, "graph.keyword.decision",
            new { traceId, allow, active = _active, intents = intents.Count });

        return allow;
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

    /// <summary>运行一次图。求解失败只告警，绝不抛出。</summary>
    public async Task RunAsync(InboundEvent @event, string traceId, Func<ActionIntent, Task> dispatch)
    {
        var intents = await RunCoreAsync(@event, traceId);

        foreach (var intent in intents)
        {
            Logging.Signal.Debug(Logging.LogGroup.Engine, "graph.keyword.intent",
                new { traceId, intent.Kind, intent.Target, active = _active });

            if (!_active)
            {
                continue;
            }

            try
            {
                await dispatch(intent);
            }
            catch (Exception ex)
            {
                // 图动作失败不得打断消息链（入库/分发已完成）
                Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.keyword.dispatch_failed",
                    new { traceId, intent.Kind, intent.Target, error = ex.Message });
            }
        }
    }

    private async Task<IReadOnlyList<ActionIntent>> RunCoreAsync(InboundEvent @event, string traceId)
    {
        if (@event.Kind != EventKind.MessageReceived)
        {
            return Array.Empty<ActionIntent>();
        }

        SolveResult result;
        try
        {
            result = _graph.Run(new EvalContext($"graph-keyword:{traceId}", DateTime.UtcNow.Ticks, @event));
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
