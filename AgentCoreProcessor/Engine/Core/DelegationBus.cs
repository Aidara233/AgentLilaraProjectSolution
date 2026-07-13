using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AgentCoreProcessor.Engine;

/// <summary>
/// 循环间消息路由总线。定向投递 + 广播分发，纯内存、无持久化。
/// </summary>
internal class DelegationBus
{
    private readonly ConcurrentDictionary<string, Action<LoopMessage>> _handlers = new();
    private readonly ConcurrentDictionary<string, byte> _activeLoops = new();

    public void RegisterLoop(string loopId, Action<LoopMessage> onMessageReceived)
    {
        _handlers[loopId] = onMessageReceived;
        _activeLoops.TryAdd(loopId, 0);
    }

    public void UnregisterLoop(string loopId)
    {
        _handlers.TryRemove(loopId, out _);
        _activeLoops.TryRemove(loopId, out _);
    }

    /// <summary>
    /// 投递消息到目标。返回已投递到的 loopId 列表。
    /// targetId=null 进行广播；指定 targetId 仅投到该循环。
    /// </summary>
    public List<string> Deliver(LoopMessage message)
    {
        var deliveredTo = new List<string>();

        if (message.TargetId != null)
        {
            if (_handlers.TryGetValue(message.TargetId, out var handler))
            {
                handler(message);
                deliveredTo.Add(message.TargetId);
            }
            // 目标不存在则丢弃（fire-and-forget，无队列回压）
        }
        else
        {
            foreach (var (loopId, _) in _activeLoops)
            {
                if (_handlers.TryGetValue(loopId, out var handler))
                {
                    handler(message);
                    deliveredTo.Add(loopId);
                }
            }
        }

        return deliveredTo;
    }

    public bool IsLoopActive(string loopId) => _activeLoops.ContainsKey(loopId);

    public IReadOnlyCollection<string> GetActiveLoopIds() =>
        _activeLoops.Keys.ToList().AsReadOnly();
}