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

    /// <summary>读取 active 开关：LILARA_GRAPH_KEYWORD_MODE=active 才真正执行意图。</summary>
    public static bool ActiveFromEnvironment() =>
        string.Equals(
            Environment.GetEnvironmentVariable("LILARA_GRAPH_KEYWORD_MODE"),
            "active",
            StringComparison.OrdinalIgnoreCase);

    public static KeywordGraphRunner? TryCreate(bool active)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Graphs", "preset.keyword-test.json");
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
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.keyword.load_failed", new { error = ex.Message });
            return null;
        }
    }

    /// <summary>运行一次图。求解失败只告警，绝不抛出。</summary>
    public async Task RunAsync(InboundEvent @event, string traceId, Func<ActionIntent, Task> dispatch)
    {
        if (@event.Kind != EventKind.MessageReceived)
        {
            return;
        }

        SolveResult result;
        try
        {
            result = _graph.Run(new EvalContext($"graph-keyword:{traceId}", DateTime.UtcNow.Ticks, @event));
        }
        catch (Exception ex)
        {
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.keyword.threw", new { traceId, error = ex.Message });
            return;
        }

        if (!result.Success)
        {
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.keyword.solve_invalid",
                new { traceId, result.Unresolved });
            return;
        }

        foreach (var intent in _graph.LastIntents)
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
