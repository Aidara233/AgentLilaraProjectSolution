using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AgentCoreProcessor.Engine;

/// <summary>
/// 委托路由总线。定向投递，非全局广播。
/// </summary>
internal class DelegationBus
{
    private readonly ConcurrentDictionary<string, Action<CrossRequest>> _handlers = new();
    private readonly ConcurrentDictionary<string, byte> _activeLoops = new();

    public void RegisterLoop(string loopId, Action<CrossRequest> onRequestReceived)
    {
        _handlers[loopId] = onRequestReceived;
        _activeLoops.TryAdd(loopId, 0);
    }

    public void UnregisterLoop(string loopId)
    {
        _handlers.TryRemove(loopId, out _);
        _activeLoops.TryRemove(loopId, out _);
    }

    /// <summary>
    /// 投递请求到目标。返回已投递到的 loopId 列表。
    /// </summary>
    public List<string> Deliver(CrossRequest request, Func<string, bool>? createLoopIfNeeded = null)
    {
        var deliveredTo = new List<string>();

        if (request.TargetId != null)
        {
            DeliverToTarget(request, request.TargetId, createLoopIfNeeded, deliveredTo);
        }
        else
        {
            DeliverBroadcast(request, deliveredTo);
        }

        return deliveredTo;
    }

    private void DeliverToTarget(CrossRequest request, string targetId,
        Func<string, bool>? createLoopIfNeeded, List<string> deliveredTo)
    {
        if (_handlers.TryGetValue(targetId, out var handler))
        {
            handler(request);
            deliveredTo.Add(targetId);
        }
        else if (createLoopIfNeeded?.Invoke(targetId) == true)
        {
            if (_handlers.TryGetValue(targetId, out var newHandler))
            {
                newHandler(request);
                deliveredTo.Add(targetId);
            }
        }
    }

    private void DeliverBroadcast(CrossRequest request, List<string> deliveredTo)
    {
        foreach (var (loopId, _) in _activeLoops)
        {
            if (loopId == LoopId.System) continue;
            if (_handlers.TryGetValue(loopId, out var handler))
            {
                var summary = CreateBroadcastSummary(request);
                handler(summary);
                deliveredTo.Add(loopId);
            }
        }
    }

    private CrossRequest CreateBroadcastSummary(CrossRequest request)
    {
        return new CrossRequest
        {
            RequestId = request.RequestId,
            InitiatorId = request.InitiatorId,
            TargetId = null,
            Title = request.Title,
            Content = request.Content.Length > 500
                ? request.Content[..500] + "..."
                : request.Content,
            State = request.State,
            SubmittedAt = request.SubmittedAt,
            ExpiresAt = request.ExpiresAt
        };
    }

    public bool IsLoopActive(string loopId) => _activeLoops.ContainsKey(loopId);

    public IReadOnlyCollection<string> GetActiveLoopIds() =>
        _activeLoops.Keys.ToList().AsReadOnly();
}
