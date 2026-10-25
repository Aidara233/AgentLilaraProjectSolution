using AgentLilara.GraphRuntime.Graph;

namespace AgentCoreProcessor.Engine.Graph;

/// <summary>把图的 SendMessage 意图送入既有 MessageEngine 出向链。</summary>
internal sealed class MessageGraphIntentDispatcher : IGraphIntentDispatcher
{
    private readonly Func<Engine.Messaging.EngineOutgoingMessage, Task> _sendOutgoing;

    public MessageGraphIntentDispatcher(Func<Engine.Messaging.EngineOutgoingMessage, Task> sendOutgoing)
    {
        _sendOutgoing = sendOutgoing;
    }

    public Task DispatchAsync(ActionIntent intent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (intent.Kind != IntentKind.SendMessage)
        {
            throw new InvalidOperationException(
                $"graph intent '{intent.Kind}' is not supported by the Message dispatcher");
        }

        var text = intent.Args.TryGetValue("text", out var cell)
            ? cell.As<string>()
            : null;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("graph send intent has empty text");
        }

        if (!int.TryParse(intent.Target, out var channelId))
        {
            throw new InvalidOperationException(
                $"graph send intent has invalid channel target '{intent.Target}'");
        }

        return _sendOutgoing(new Engine.Messaging.EngineOutgoingMessage
        {
            ChannelId = channelId,
            Content = text,
            SentByBot = true,
            InternalOnly = false,
        });
    }
}
