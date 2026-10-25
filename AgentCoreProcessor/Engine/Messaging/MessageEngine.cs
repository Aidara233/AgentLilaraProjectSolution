// AgentCoreProcessor/Engine/Messaging/MessageEngine.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine.Messaging;

/// <summary>
/// 双向消息枢纽。入向入库+去重+分发，出向入库+路由。
/// </summary>
internal class MessageEngine : ISubEngine
{
    public string EngineType => "Message";
    public bool IsAlive { get; private set; } = true;
    public bool IsInfrastructure => true;

    private readonly ISystemContext _ctx;
    private readonly ConcurrentDictionary<(int channelId, string subStream), List<MessageInputPort>> _subscribers = new();

    public MessageEngine(ISystemContext ctx)
    {
        _ctx = ctx;
    }

    public IInputPort Subscribe(int channelId, string subStream, int weight = 5)
    {
        var port = new MessageInputPort(channelId, subStream, weight);
        _subscribers.GetOrAdd((channelId, subStream), _ => new()).Add(port);
        return port;
    }

    public void Unsubscribe(IInputPort port)
    {
        if (port is MessageInputPort mp && _subscribers.TryGetValue((mp.ChannelId, mp.SubStream), out var list))
            list.Remove(mp);
    }

    /// <summary>入向入口：AdapterEngine 调用</summary>
    public async Task AcceptIncoming(EngineIncomingMessage msg)
    {
        // DB 去重
        var existing = await _ctx.Session.FindMessageByPlatformIdAsync(msg.ChannelId, msg.MessageId);
        if (existing != null) return;

        // 入库
        await _ctx.Session.InsertMessageAsync(new UserMessage
        {
            ChannelId = msg.ChannelId,
            Content = msg.Content,
            PlatformMessageId = msg.MessageId,
            UserId = msg.SenderUserId,
            Time = DateTime.UtcNow,
            IsFromBot = false
        });

        // 图在入库之后观察消息。默认 shadow：只记录意图；active：进入既有出向链。
        // 注意：这里只走 WebUI 注入路径；真实适配器入站走 ExpressEngineSpawnCheck。

        // 按 tag 分发到子流
        Distribute(msg.ChannelId, msg.Tag, msg);
    }

    /// <summary>
    /// 投递已入库的消息到被占用频道的 session 端口（入向接通）。
    /// 由 RouteMessageAsync 在频道被 session full-claim 时调用：消息已经过 SessionManager
    /// 完整入库+身份映射，这里只负责分发到端口，不重复入库/去重。
    /// 根据 isMentioned 分投到 "mention" (Weight=100, 立即激活) 或 "normal" (Weight=5, 攒多条) 子流。
    /// </summary>
    public void DeliverToSession(int channelId, UserMessage msg, bool isMentioned)
    {
        var subStream = isMentioned ? "mention" : "normal";
        var key = (channelId, subStream);
        var count = _subscribers.TryGetValue(key, out var ports) ? ports.Count : 0;
        Signal.Debug(LogGroup.Engine, "DeliverToSession 分发",
            new { channelId, msgId = msg.Id, subStream, isMentioned, subscriberCount = count });
        Distribute(channelId, subStream, msg);
    }

    /// <summary>出向入口：Session 工具调用后写入</summary>
    public async Task SendOutgoing(EngineOutgoingMessage msg)
    {
        await _ctx.Session.InsertMessageAsync(new UserMessage
        {
            ChannelId = msg.ChannelId,
            Content = msg.Content,
            Time = DateTime.UtcNow,
            IsFromBot = true
        });

        if (!msg.InternalOnly)
            OnOutgoingReady?.Invoke(msg);
        else
            Distribute(msg.ChannelId, "internal", msg);
    }

    /// <summary>出向消息就绪时触发，AdapterEngine 订阅</summary>
    public event Action<EngineOutgoingMessage>? OnOutgoingReady;

    private void Distribute(int channelId, string subStream, object data)
    {
        if (_subscribers.TryGetValue((channelId, subStream), out var ports))
        {
            foreach (var port in ports)
                port.Enqueue(data);
        }
    }

    public Task RunAsync() => Task.CompletedTask;
    public void OnEvent(EngineEvent e) { }
    public void RequestStop() => IsAlive = false;
}

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
