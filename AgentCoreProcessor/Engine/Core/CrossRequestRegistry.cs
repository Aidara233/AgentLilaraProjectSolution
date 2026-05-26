using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine;

/// <summary>
/// 跨循环请求注册表。统一管理请求的完整生命周期、持久化和可见性。
/// 替换 DelegationRegistry + TaskBridge 任务队列。
/// </summary>
internal class CrossRequestRegistry
{
    private const int CompactThreshold = 200;

    private readonly ConcurrentDictionary<string, CrossRequest> _requests = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CrossRequestResponse>> _responses = new();
    private readonly string _journalPath;
    private readonly object _persistLock = new();
    private readonly DelegationBus _bus;

    private readonly object _eventLock = new();
    private event Action<string>? _onRequestUpdated;
    private event Action<string>? _onRequestSubmitted;
    private event Action<CrossRequest>? _onRequestCompleted;

    /// <summary>请求状态变更时触发。参数：受影响的 loopId。</summary>
    public event Action<string> OnRequestUpdated
    {
        add { lock (_eventLock) _onRequestUpdated += value; }
        remove { lock (_eventLock) _onRequestUpdated -= value; }
    }

    /// <summary>请求提交（需要路由）时触发。参数：发起者 loopId。</summary>
    public event Action<string> OnRequestSubmitted
    {
        add { lock (_eventLock) _onRequestSubmitted += value; }
        remove { lock (_eventLock) _onRequestSubmitted -= value; }
    }

    /// <summary>请求完成/拒绝时触发。参数：完整请求对象。</summary>
    public event Action<CrossRequest> OnRequestCompleted
    {
        add { lock (_eventLock) _onRequestCompleted += value; }
        remove { lock (_eventLock) _onRequestCompleted -= value; }
    }

    private void FireRequestUpdated(string loopId)
    {
        Action<string>? handler;
        lock (_eventLock) { handler = _onRequestUpdated; }
        try { handler?.Invoke(loopId); }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "OnRequestUpdated handler异常", new { loopId, error = ex.Message });
        }
    }

    private void FireRequestSubmitted(string loopId)
    {
        Action<string>? handler;
        lock (_eventLock) { handler = _onRequestSubmitted; }
        try { handler?.Invoke(loopId); }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "OnRequestSubmitted handler异常", new { loopId, error = ex.Message });
        }
    }

    private void FireRequestCompleted(CrossRequest request)
    {
        Action<CrossRequest>? handler;
        lock (_eventLock) { handler = _onRequestCompleted; }
        try { handler?.Invoke(request); }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "OnRequestCompleted handler异常",
                new { requestId = request.RequestId, error = ex.Message });
        }
    }

    public CrossRequestRegistry(string storagePath, DelegationBus bus)
    {
        _journalPath = Path.Combine(storagePath, "cross_requests.jsonl");
        _bus = bus;
        Load();
    }

    // ═══════ 提交 ═══════

    public CrossRequest Submit(string initiatorId, string? targetId,
        string title, string content, TimeSpan timeout)
    {
        var request = new CrossRequest
        {
            InitiatorId = initiatorId,
            TargetId = targetId,
            Title = title,
            Content = content,
            ExpiresAt = DateTime.Now + timeout,
            TraceSignalId = SignalContext.Current?.SignalId,
            TraceParentSpanId = SignalContext.Current?.CurrentSpanId
        };

        _requests[request.RequestId] = request;
        _responses[request.RequestId] = new ConcurrentDictionary<string, CrossRequestResponse>();

        Signal.Event(LogGroup.Engine, "委托提交",
            new { requestId = request.RequestId[..8], initiatorId, targetId, title });

        AppendToJournal(request);
        FireRequestSubmitted(initiatorId);
        _bus.Deliver(request);

        return request;
    }

    // ═══════ 回应 ═══════

    public bool Respond(string requestId, string responderId,
        CrossRequestResponseType type, string content)
    {
        if (!_requests.TryGetValue(requestId, out var request))
        {
            Signal.Warn(LogGroup.Engine, "委托Respond失败:请求不存在", new { requestId, responderId, type });
            return false;
        }
        if (request.State is CrossRequestState.Archived or CrossRequestState.Timeout)
        {
            Signal.Warn(LogGroup.Engine, "委托Respond失败:状态不可操作",
                new { requestId, responderId, type, state = request.State.ToString() });
            return false;
        }

        Signal.Event(LogGroup.Engine, "委托状态变更",
            new { requestId = requestId[..8], responderId, type = type.ToString(),
                  title = request.Title, prevState = request.State.ToString() });

        var response = new CrossRequestResponse
        {
            SequenceNumber = GetNextSequence(requestId),
            ResponderId = responderId,
            Type = type,
            Content = content
        };

        _responses[requestId][response.SequenceNumber.ToString()] = response;
        request.Responses.Add(response);

        switch (type)
        {
            case CrossRequestResponseType.Accept:
                request.State = CrossRequestState.Accepted;
                break;
            case CrossRequestResponseType.Reject:
                request.State = CrossRequestState.Rejected;
                request.CompletedAt = DateTime.Now;
                break;
            case CrossRequestResponseType.Progress:
                request.State = CrossRequestState.InProgress;
                break;
            case CrossRequestResponseType.Complete:
                request.State = CrossRequestState.Completed;
                request.CompletedAt = DateTime.Now;
                break;
            case CrossRequestResponseType.Failed:
                request.State = CrossRequestState.Failed;
                request.CompletedAt = DateTime.Now;
                break;
            case CrossRequestResponseType.Ignore:
                break;
        }

        AppendToJournal(request);
        FireRequestUpdated(request.InitiatorId);
        if (type == CrossRequestResponseType.Accept && responderId != request.InitiatorId)
            FireRequestUpdated(responderId);

        if (type is CrossRequestResponseType.Complete or CrossRequestResponseType.Failed or CrossRequestResponseType.Reject)
            FireRequestCompleted(request);

        return true;
    }

    // ═══════ 状态切换 ═══════

    public void Idle(string requestId)
    {
        if (_requests.TryGetValue(requestId, out var r)
            && r.State is not CrossRequestState.Archived)
        {
            r.State = CrossRequestState.Idle;
            AppendToJournal(r);
        }
    }

    public void Archive(string requestId)
    {
        if (!_requests.TryGetValue(requestId, out var r)) return;
        if (r.State is CrossRequestState.Archived) return;

        r.State = CrossRequestState.Archived;
        r.CompletedAt ??= DateTime.Now;
        AppendToJournal(r);

        // 通知所有接受者委托已被归档
        var acceptedResponders = r.Responses
            .Where(resp => resp.Type == CrossRequestResponseType.Accept)
            .Select(resp => resp.ResponderId)
            .Distinct()
            .ToList();

        foreach (var responderId in acceptedResponders)
        {
            if (responderId != r.InitiatorId)
                FireRequestUpdated(responderId);
        }
        FireRequestUpdated(r.InitiatorId);
    }

    // ═══════ 超时检查 ═══════

    public void EnforceTimeouts()
    {
        var now = DateTime.Now;
        foreach (var (_, request) in _requests)
        {
            if (request.ExpiresAt <= now
                && (request.State is CrossRequestState.Submitted
                    or CrossRequestState.Accepted
                    or CrossRequestState.InProgress))
            {
                request.State = CrossRequestState.Timeout;
                request.CompletedAt = now;
                AppendToJournal(request);
                FireRequestUpdated(request.InitiatorId);
            }
        }
    }

    // ═══════ 可见性查询 ═══════

    public List<CrossRequest> GetVisible(string loopId)
    {
        return _requests.Values.Where(r =>
            (r.State is CrossRequestState.Submitted
                or CrossRequestState.Accepted
                or CrossRequestState.InProgress
                or CrossRequestState.Rejected)
            && IsVisibleTo(r, loopId)
            && !HasBeenIgnored(r.RequestId, loopId)
        ).ToList();
    }

    public List<CrossRequest> GetIdle(string loopId)
    {
        return _requests.Values.Where(r =>
            r.State == CrossRequestState.Idle
            && WasAcceptedBy(r.RequestId, loopId)
        ).ToList();
    }

    public List<CrossRequest> GetCompleted(string loopId)
    {
        return _requests.Values.Where(r =>
            (r.State == CrossRequestState.Completed || r.State == CrossRequestState.Failed)
            && (r.InitiatorId == loopId
                || r.TargetId == loopId
                || WasAcceptedBy(r.RequestId, loopId))
        ).ToList();
    }

    public CrossRequest? Get(string requestId) =>
        _requests.TryGetValue(requestId, out var r) ? r : null;

    /// <summary>返回所有请求（供 WebUI 监视用，绕过可见性过滤）。</summary>
    public List<CrossRequest> GetAll() => _requests.Values.ToList();

    // ═══════ 内部 ═══════

    private static bool IsVisibleTo(CrossRequest r, string loopId)
    {
        return r.InitiatorId == loopId
            || r.TargetId == loopId
            || r.TargetId == null; // 广播对所有循环可见
    }

    private bool HasBeenIgnored(string requestId, string loopId)
    {
        if (!_responses.TryGetValue(requestId, out var responses)) return false;
        return responses.Values.Any(r =>
            r.ResponderId == loopId && r.Type == CrossRequestResponseType.Ignore);
    }

    private bool WasAcceptedBy(string requestId, string loopId)
    {
        if (!_responses.TryGetValue(requestId, out var responses)) return false;
        return responses.Values.Any(r =>
            r.ResponderId == loopId && r.Type == CrossRequestResponseType.Accept);
    }

    private int GetNextSequence(string requestId)
    {
        if (!_responses.TryGetValue(requestId, out var dict)) return 0;
        return dict.Count;
    }

    // ═══════ 持久化：JSONL 追加 ═══════

    private void AppendToJournal(CrossRequest request)
    {
        lock (_persistLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_journalPath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var line = JsonConvert.SerializeObject(request, Formatting.None,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                File.AppendAllText(_journalPath, line + "\n");
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "CrossRequest持久化失败", new { error = ex.Message });
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_journalPath)) return;
            // 最后一行代表每个 RequestId 的最终状态
            var latest = new Dictionary<string, CrossRequest>();
            foreach (var line in File.ReadLines(_journalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var req = JsonConvert.DeserializeObject<CrossRequest>(line);
                    if (req == null) continue;

                    if (req.State == CrossRequestState.Submitted)
                    {
                        req.State = CrossRequestState.Timeout;
                        req.CompletedAt = req.ExpiresAt;
                    }
                    if (req.State == CrossRequestState.InProgress
                        || req.State == CrossRequestState.Accepted)
                        req.State = CrossRequestState.Submitted;

                    latest[req.RequestId] = req;
                }
                catch (Exception ex) { Signal.Warn(LogGroup.Engine, "跨请求日志解析失败", new { error = ex.Message }); }
            }

            foreach (var (id, req) in latest)
            {
                _requests[id] = req;
                _responses[id] = new ConcurrentDictionary<string, CrossRequestResponse>();
                foreach (var resp in req.Responses)
                    _responses[id][resp.SequenceNumber.ToString()] = resp;
            }
        }
        catch (Exception ex)
        {
            Signal.Error(LogGroup.Engine, "CrossRequest加载失败", new { error = ex.Message });
        }
    }

    public void Cleanup(TimeSpan maxAge)
    {
        var cutoff = DateTime.Now - maxAge;
        var toRemove = _requests.Values
            .Where(r => r.State is CrossRequestState.Archived or CrossRequestState.Timeout
                        && r.CompletedAt < cutoff)
            .Select(r => r.RequestId)
            .ToList();

        foreach (var id in toRemove)
        {
            _requests.TryRemove(id, out _);
            _responses.TryRemove(id, out _);
        }

        if (toRemove.Count > CompactThreshold)
            CompactJournal();
    }

    private void CompactJournal()
    {
        lock (_persistLock)
        {
            try
            {
                File.WriteAllText(_journalPath, "");
                foreach (var req in _requests.Values.OrderBy(r => r.SubmittedAt))
                {
                    var line = JsonConvert.SerializeObject(req, Formatting.None,
                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                    File.AppendAllText(_journalPath, line + "\n");
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "journal压缩失败", new { error = ex.Message });
            }
        }
    }
}
