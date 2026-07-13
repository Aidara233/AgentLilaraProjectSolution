using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Component;

/// <summary>
/// IAgentMessaging 实现。每个循环一个实例，封装 DelegationBus 投递 + 本地通知队列。
/// 消息模型：fire-and-forget，无状态机、无 response。
/// </summary>
internal class AgentMessagingImpl : IAgentMessaging
{
    private readonly string _myLoopId;
    private readonly DelegationBus _bus;
    private readonly Action _wakeMe;
    private readonly Func<string, bool> _isTargetAlive;
    private readonly Func<List<string>> _getActiveLoopIds;

    /// <summary>接收到的消息队列（待 DrainNotifications/Receive 消费）。</summary>
    private readonly ConcurrentQueue<LoopMessage> _inbox = new();

    /// <summary>是否有待注入的消息。</summary>
    public bool HasPendingNotifications => !_inbox.IsEmpty;

    public AgentMessagingImpl(string myLoopId, DelegationBus bus,
        Action wakeMe, Func<string, bool> isTargetAlive, Func<List<string>>? getActiveLoopIds = null)
    {
        _myLoopId = myLoopId;
        _bus = bus;
        _wakeMe = wakeMe;
        _isTargetAlive = isTargetAlive;
        _getActiveLoopIds = getActiveLoopIds ?? (() => new List<string> { myLoopId });
    }

    /// <summary>取消订阅，引擎关闭时调用。</summary>
    public void Detach()
    {
        // 当前订阅通过 DelegationBus.RegisterLoop/UnregisterLoop 管理，此处保留以便未来扩展
    }

    // ═══════ 发送 ═══════

    public string SendMessage(string? targetId, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("content 不能为空", nameof(content));

        if (targetId != null && !_isTargetAlive(targetId))
        {
            Signal.Warn(LogGroup.Engine, "SendMessage: 目标循环不可用",
                new { initiatorId = _myLoopId, targetId });
            // 仍发出，但 DelegationBus 会丢弃。返回的 Id 仅供日志用
            return Guid.NewGuid().ToString("N");
        }

        var msg = new LoopMessage
        {
            InitiatorId = _myLoopId,
            TargetId = targetId,
            Content = content
        };

        Signal.Event(LogGroup.Engine, "SendMessage",
            new { messageId = msg.MessageId[..8], initiatorId = _myLoopId, targetId, contentLen = content.Length });

        _bus.Deliver(msg);
        return msg.MessageId;
    }

    // ═══════ 接收 ═══════

    public List<LoopMessageInfo> Receive(int maxCount = 10)
    {
        var list = new List<LoopMessageInfo>();
        while (list.Count < maxCount && _inbox.TryDequeue(out var msg))
        {
            list.Add(ToInfo(msg));
        }
        return list;
    }

    /// <summary>排出待注入的通知消息（由通知模块消费，全清）。</summary>
    public List<LoopMessageInfo> DrainNotifications()
    {
        var list = new List<LoopMessageInfo>();
        while (_inbox.TryDequeue(out var msg))
            list.Add(ToInfo(msg));
        return list;
    }

    /// <summary>DelegationBus 回调：收到定向消息或广播时入队。</summary>
    internal void EnqueueIncoming(LoopMessage msg)
    {
        _inbox.Enqueue(msg);
        _wakeMe();
    }

    public List<string> GetActiveLoopIds() => _getActiveLoopIds();

    // ═══════ 映射 ═══════

    private static LoopMessageInfo ToInfo(LoopMessage m) => new()
    {
        MessageId = m.MessageId,
        InitiatorId = m.InitiatorId,
        TargetId = m.TargetId,
        Content = m.Content,
        Timestamp = m.Timestamp
    };
}