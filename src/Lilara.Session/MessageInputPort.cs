using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine.Messaging;

/// <summary>
/// MessageEngine 的内部输入端口实现，基于 Channel<T>。
/// </summary>
internal class MessageInputPort : IInputPort
{
    private readonly System.Threading.Channels.Channel<object> _channel = System.Threading.Channels.Channel.CreateUnbounded<object>();
    private int _queueCount;

    public int ChannelId { get; }
    public string SubStream { get; }
    public string Name => $"msg:{ChannelId}:{SubStream}";
    public Type DataType => typeof(object);
    public int Weight { get; }

    /// <summary>当前队列中待处理的消息数量（用于权重累积激活判定）。</summary>
    public int QueueCount => Volatile.Read(ref _queueCount);

    public MessageInputPort(int channelId, string subStream, int weight = 5)
    {
        ChannelId = channelId;
        SubStream = subStream;
        Weight = weight;
    }

    public void Enqueue(object data)
    {
        _channel.Writer.TryWrite(data);
        Interlocked.Increment(ref _queueCount);
    }

    /// <summary>在激活判定后重置计数器（消息仍在 channel 中等待 DrainInputPortsRaw 消费）。</summary>
    public void ResetQueueCount() => Interlocked.Exchange(ref _queueCount, 0);

    public async Task<object?> ReceiveAsync(CancellationToken ct)
    {
        try { return await _channel.Reader.ReadAsync(ct); }
        catch (OperationCanceledException) { return null; }
    }

    public bool TryPeek(out object? data) => _channel.Reader.TryPeek(out data);
}
