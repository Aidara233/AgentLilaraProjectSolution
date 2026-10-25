using AgentCoreProcessor.Adapter;
using AgentLilara.GraphRuntime.Graph;

namespace AgentCoreProcessor.Engine.Graph;

/// <summary>
/// 把图的 SendMessage 意图送到**真实出向出口**（AdapterManager → IAdapter.SendMessageAsync）。
/// <para>⚠️ 不走 <c>MessageEngine.OnOutgoingReady</c>：那条事件全仓无人订阅，只入库不发送。</para>
/// </summary>
internal sealed class MessageGraphIntentDispatcher : IGraphIntentDispatcher
{
    private readonly Func<OutgoingMessage, Task<string?>> _send;
    private readonly int _channelId;

    public MessageGraphIntentDispatcher(Func<OutgoingMessage, Task<string?>> send, int channelId)
    {
        _send = send;
        _channelId = channelId;
    }

    public async Task DispatchAsync(ActionIntent intent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (intent.Kind != IntentKind.SendMessage)
        {
            throw new InvalidOperationException(
                $"graph intent '{intent.Kind}' is not supported by the message dispatcher");
        }

        var text = intent.Args.TryGetValue("text", out var cell) ? cell.As<string>() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("graph send intent has empty text");
        }

        var result = await _send(new OutgoingMessage
        {
            ChannelId = _channelId.ToString(),
            Content = text,
        });

        if (result is null)
        {
            throw new InvalidOperationException($"no adapter could send to channel {_channelId}");
        }
    }
}
