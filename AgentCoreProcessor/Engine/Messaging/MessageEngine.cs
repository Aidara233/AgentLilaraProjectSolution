// AgentCoreProcessor/Engine/Messaging/MessageEngine.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading;
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

        // 按 tag 分发到子流
        Distribute(msg.ChannelId, msg.Tag, msg);
    }

    /// <summary>
    /// 投递已入库的消息到被占用频道的 session 端口（入向接通）。
    /// 由 RouteMessageAsync 在频道被 session full-claim 时调用：消息已经过 SessionManager
    /// 完整入库+身份映射，这里只负责分发到端口，不重复入库/去重。subStream 固定 "normal"
    /// （escalate session 端口订阅的子流，见 ChannelExpressCycle PortSpec）。
    /// </summary>
    public void DeliverToSession(int channelId, UserMessage msg)
    {
        var key = (channelId, "normal");
        var count = _subscribers.TryGetValue(key, out var ports) ? ports.Count : 0;
        Signal.Debug(LogGroup.Engine, "DeliverToSession 分发",
            new { channelId, msgId = msg.Id, subStream = "normal", subscriberCount = count });
        Distribute(channelId, "normal", msg);
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

    public int ChannelId { get; }
    public string SubStream { get; }
    public string Name => $"msg:{ChannelId}:{SubStream}";
    public Type DataType => typeof(object);
    public int Weight { get; }

    public MessageInputPort(int channelId, string subStream, int weight = 5)
    {
        ChannelId = channelId;
        SubStream = subStream;
        Weight = weight;
    }

    public void Enqueue(object data) => _channel.Writer.TryWrite(data);

    public async Task<object?> ReceiveAsync(CancellationToken ct)
    {
        try { return await _channel.Reader.ReadAsync(ct); }
        catch (OperationCanceledException) { return null; }
    }

    public bool TryPeek(out object? data) => _channel.Reader.TryPeek(out data);
}
