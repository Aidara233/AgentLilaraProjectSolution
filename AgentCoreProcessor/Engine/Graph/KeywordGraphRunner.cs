using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;

namespace AgentCoreProcessor.Engine.Graph;

/// <summary>
/// 关键词图运行器：把一个真实入站消息送进 preset.keyword-test。
/// 默认只影子运行；只有显式启用时才返回可执行的意图。
/// </summary>
internal sealed class KeywordGraphRunner
{
    private const string Keyword = "测试";
    private readonly GraphInstance _graph;
    private readonly bool _active;

    private KeywordGraphRunner(GraphInstance graph, bool active)
    {
        _graph = graph;
        _active = active;
    }

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
            AgentCoreProcessor.Logging.Signal.Warn(
                AgentCoreProcessor.Logging.LogGroup.Engine,
                "graph.keyword.load_failed",
                new { error = ex.Message });
            return null;
        }
    }

    public async Task RunAsync(Engine.Messaging.EngineIncomingMessage message, Func<ActionIntent, Task> dispatch)
    {
        var @event = InboundEvent.Message(
            adapterId: "alp",
            channelKey: message.ChannelId.ToString(),
            text: message.Content,
            channelKind: AgentLilara.GraphRuntime.Contracts.ChannelKind.Group,
            senderId: message.SenderUserId.ToString(),
            mentionsBot: string.Equals(message.Tag, "mention", StringComparison.OrdinalIgnoreCase),
            at: message.Timestamp == default ? DateTimeOffset.UtcNow : new DateTimeOffset(message.Timestamp));

        var result = _graph.Run(new EvalContext(
            $"graph-keyword:{message.MessageId}",
            DateTime.UtcNow.Ticks,
            @event));

        if (!result.Success)
        {
            AgentCoreProcessor.Logging.Signal.Warn(
                AgentCoreProcessor.Logging.LogGroup.Engine,
                "graph.keyword.solve_invalid",
                new { message.MessageId, result.Unresolved });
            return;
        }

        foreach (var intent in _graph.LastIntents)
        {
            AgentCoreProcessor.Logging.Signal.Debug(
                AgentCoreProcessor.Logging.LogGroup.Engine,
                "graph.keyword.intent",
                new { message.MessageId, intent.Kind, intent.Target, active = _active });

            if (_active)
            {
                await dispatch(intent);
            }
        }
    }

    public static bool IsKeywordPreset(string text) =>
        text.Contains(Keyword, StringComparison.Ordinal);
}
