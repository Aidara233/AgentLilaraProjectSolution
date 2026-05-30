using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK.Services;
using CrossReqType = AgentLilara.PluginSDK.Services.CrossRequestResponseType;
using EngineCrossReqType = AgentCoreProcessor.Engine.CrossRequestResponseType;

namespace AgentCoreProcessor.Component;

/// <summary>
/// IAgentMessaging 实现。每个循环一个实例，封装 CrossRequestRegistry + DelegationBus。
/// </summary>
internal class AgentMessagingImpl : IAgentMessaging
{
    private readonly string _myLoopId;
    private readonly CrossRequestRegistry _registry;
    private readonly Action _wakeMe;
    private readonly Func<string, bool> _isTargetAlive;
    private readonly Func<List<string>> _getActiveLoopIds;

    private readonly ConcurrentQueue<DelegationNotification> _notifications = new();
    private readonly ConcurrentDictionary<string, int> _lastNotifiedSeq = new();

    /// <summary>是否有待处理的委托通知。</summary>
    public bool HasPendingNotifications => !_notifications.IsEmpty;

    public AgentMessagingImpl(string myLoopId, CrossRequestRegistry registry,
        Action wakeMe, Func<string, bool> isTargetAlive, Func<List<string>>? getActiveLoopIds = null)
    {
        _myLoopId = myLoopId;
        _registry = registry;
        _wakeMe = wakeMe;
        _isTargetAlive = isTargetAlive;
        _getActiveLoopIds = getActiveLoopIds ?? (() => new List<string> { myLoopId });

        _registry.OnRequestUpdated += OnRegistryUpdated;
    }

    /// <summary>取消订阅，引擎关闭时调用。</summary>
    public void Detach()
    {
        _registry.OnRequestUpdated -= OnRegistryUpdated;
    }

    private void OnRegistryUpdated(string loopId)
    {
        if (loopId != _myLoopId) return;

        try
        {
            var myRequests = _registry.GetAll()
                .Where(r => r.InitiatorId == _myLoopId && r.Responses.Count > 0)
                .ToList();

            var enqueued = false;
            foreach (var req in myRequests)
            {
                if (req.State == CrossRequestState.Idle) continue;

                var lastKnownSeq = _lastNotifiedSeq.GetValueOrDefault(req.RequestId, -1);

                foreach (var resp in req.Responses.ToList())
                {
                    if (resp.SequenceNumber <= lastKnownSeq) continue;
                    if (resp.Type == EngineCrossReqType.Ignore) continue;
                    if (resp.ResponderId == _myLoopId) continue;

                    _notifications.Enqueue(new DelegationNotification
                    {
                        RequestId = req.RequestId,
                        Title = req.Title,
                        NewState = req.State,
                        ResponseType = resp.Type,
                        ResponderId = resp.ResponderId,
                        Content = resp.Content,
                        Timestamp = resp.Timestamp
                    });
                    enqueued = true;
                    _lastNotifiedSeq[req.RequestId] = resp.SequenceNumber;
                }
            }

            if (enqueued)
            {
                Signal.Event(LogGroup.Engine, "委托通知入队",
                    new { loopId = _myLoopId, count = _notifications.Count });
                _wakeMe();
            }
        }
        catch (Exception ex)
        {
            Signal.Error(LogGroup.Engine, "委托通知处理异常",
                new { loopId = _myLoopId, error = ex.Message });
        }
    }

    // ═══════ 提交 ═══════

    public async Task<CrossRequestResult> SubmitAndWaitAsync(
        string? targetId, string title, string content,
        Dictionary<string, string>? metadata = null,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(45);

        Signal.Event(LogGroup.Engine, "SubmitAndWaitAsync", new { initiatorId = _myLoopId, targetId, title });

        if (targetId != null && !_isTargetAlive(targetId))
        {
            return new CrossRequestResult
            {
                Success = false,
                TimedOut = false,
                Verdict = "target_unavailable",
                Result = $"目标循环 {targetId} 不可用"
            };
        }

        var request = _registry.Submit(_myLoopId, targetId, title, content, effectiveTimeout);

        var tcs = new TaskCompletionSource<CrossRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        var completed = false;

        void onUpdated(string lid)
        {
            if (completed) return;
            if (lid != _myLoopId) return;
            var req = _registry.Get(request.RequestId);
            if (req == null) return;

            CrossRequestResult? result = null;

            switch (req.State)
            {
                case CrossRequestState.Accepted:
                    result = new CrossRequestResult { RequestId = req.RequestId, Success = true, Verdict = "accepted" };
                    break;
                case CrossRequestState.Rejected:
                    result = new CrossRequestResult { RequestId = req.RequestId, Success = false, Verdict = "rejected",
                        Result = req.Responses.LastOrDefault()?.Content };
                    break;
                case CrossRequestState.Completed:
                    result = new CrossRequestResult { RequestId = req.RequestId, Success = true, Verdict = "completed",
                        Result = req.Responses.LastOrDefault(r => r.Type == EngineCrossReqType.Complete)?.Content };
                    break;
                case CrossRequestState.Failed:
                    result = new CrossRequestResult { RequestId = req.RequestId, Success = false, Verdict = "failed",
                        Result = req.Responses.LastOrDefault()?.Content };
                    break;
                case CrossRequestState.Timeout:
                    result = new CrossRequestResult { RequestId = req.RequestId, Success = false, TimedOut = true, Verdict = "timeout" };
                    break;
                case CrossRequestState.Archived:
                    result = new CrossRequestResult { RequestId = req.RequestId, Success = false, Verdict = "archived",
                        Result = "委托已被归档" };
                    break;
            }

            if (result != null)
            {
                completed = true;
                tcs.TrySetResult(result);
            }
        }

        _registry.OnRequestUpdated += onUpdated;
        onUpdated(_myLoopId);

        try
        {
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(effectiveTimeout, cts.Token));
            if (completedTask == tcs.Task)
                return await tcs.Task;

            _registry.Idle(request.RequestId);
            return new CrossRequestResult
            {
                RequestId = request.RequestId,
                Success = false,
                TimedOut = true,
                Verdict = "timeout",
                Result = "等待回应超时"
            };
        }
        finally
        {
            _registry.OnRequestUpdated -= onUpdated;
        }
    }

    public string SubmitFireAndForget(string? targetId, string title, string content)
    {
        var request = _registry.Submit(_myLoopId, targetId, title, content, TimeSpan.FromMinutes(10));
        return request.RequestId;
    }

    // ═══════ 接收/回应 ═══════

    public List<CrossRequestInfo> Receive(int maxCount = 10)
    {
        return _registry.GetVisible(_myLoopId)
            .Take(maxCount)
            .Select(ToInfo)
            .ToList();
    }

    public bool Respond(string requestId, CrossReqType type, string content)
    {
        return _registry.Respond(requestId, _myLoopId, MapType(type), content);
    }

    // ═══════ 通知队列 ═══════

    /// <summary>将收到的跨循环请求入队为一次性通知（由 ChannelEngine drain 调用）。</summary>
    internal void EnqueueIncoming(CrossRequest request)
    {
        _notifications.Enqueue(new DelegationNotification
        {
            RequestId = request.RequestId,
            Title = request.Title,
            NewState = request.State,
            ResponseType = EngineCrossReqType.Accept, // 标记位，FormatState 按 state=Submitted 识别
            ResponderId = request.InitiatorId,
            Content = request.Content,
            Timestamp = request.SubmittedAt
        });
    }

    public List<DelegationNotificationInfo> DrainNotifications()
    {
        var list = new List<DelegationNotificationInfo>();
        while (_notifications.TryDequeue(out var n))
        {
            list.Add(new DelegationNotificationInfo
            {
                RequestId = n.RequestId,
                Title = n.Title,
                NewState = n.NewState.ToString(),
                ResponseType = n.ResponseType.ToString(),
                ResponderId = n.ResponderId,
                Content = n.Content,
                Timestamp = n.Timestamp
            });
        }
        return list;
    }

    // ═══════ 查询 ═══════

    public List<CrossRequestInfo> GetActiveRequests()
        => _registry.GetVisible(_myLoopId).Select(ToInfo).ToList();

    public List<CrossRequestInfo> GetCompletedRequests()
        => _registry.GetCompleted(_myLoopId).Select(ToInfo).ToList();

    public List<CrossRequestInfo> GetArchivedRequests()
        => _registry.GetVisible(_myLoopId)
            .Where(r => r.State == CrossRequestState.Archived)
            .Select(ToInfo).ToList();

    public void Archive(string requestId) => _registry.Archive(requestId);

    public void Ignore(string requestId)
        => _registry.Respond(requestId, _myLoopId, EngineCrossReqType.Ignore, "ignored");

    public CrossRequestInfo? Get(string requestId)
    {
        var r = _registry.Get(requestId);
        return r != null ? ToInfo(r) : null;
    }

    public List<string> GetActiveLoopIds() => _getActiveLoopIds();

    // ═══════ 映射 ═══════

    private static EngineCrossReqType MapType(CrossReqType t) => t switch
    {
        CrossReqType.Accept => EngineCrossReqType.Accept,
        CrossReqType.Reject => EngineCrossReqType.Reject,
        CrossReqType.Progress => EngineCrossReqType.Progress,
        CrossReqType.Complete => EngineCrossReqType.Complete,
        CrossReqType.Failed => EngineCrossReqType.Failed,
        CrossReqType.Ignore => EngineCrossReqType.Ignore,
        _ => EngineCrossReqType.Complete
    };

    private static CrossRequestInfo ToInfo(CrossRequest r) => new()
    {
        Id = r.RequestId,
        InitiatorId = r.InitiatorId,
        TargetId = r.TargetId,
        Title = r.Title,
        Content = r.Content,
        State = r.State.ToString(),
        SubmittedAt = r.SubmittedAt,
        ExpiresAt = r.ExpiresAt,
        CompletedAt = r.CompletedAt,
        Responses = r.Responses.Select(resp => new CrossRequestResponseInfo
        {
            Seq = resp.SequenceNumber,
            ResponderId = resp.ResponderId,
            Type = resp.Type.ToString(),
            Content = resp.Content,
            Timestamp = resp.Timestamp
        }).ToList()
    };
}
