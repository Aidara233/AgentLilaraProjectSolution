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

    /// <summary>请求状态变更时触发。参数：受影响的 loopId。</summary>
    public Action<string>? OnRequestUpdated;

    /// <summary>请求提交（需要路由）时触发。参数：发起者 loopId。</summary>
    public Action<string>? OnRequestSubmitted;

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
            ExpiresAt = DateTime.UtcNow + timeout,
            TraceSignalId = SignalContext.Current?.SignalId,
            TraceParentSpanId = SignalContext.Current?.CurrentSpanId
        };

        _requests[request.RequestId] = request;
        _responses[request.RequestId] = new ConcurrentDictionary<string, CrossRequestResponse>();

        AppendToJournal(request);
        OnRequestSubmitted?.Invoke(initiatorId);

        return request;
    }

    // ═══════ 回应 ═══════

    public bool Respond(string requestId, string responderId,
        CrossRequestResponseType type, string content)
    {
        if (!_requests.TryGetValue(requestId, out var request))
            return false;
        if (request.State is CrossRequestState.Archived or CrossRequestState.Timeout)
            return false;

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
                request.CompletedAt = DateTime.UtcNow;
                break;
            case CrossRequestResponseType.Progress:
                request.State = CrossRequestState.InProgress;
                break;
            case CrossRequestResponseType.Complete:
                request.State = CrossRequestState.Completed;
                request.CompletedAt = DateTime.UtcNow;
                break;
            case CrossRequestResponseType.Ignore:
                break;
        }

        AppendToJournal(request);
        OnRequestUpdated?.Invoke(request.InitiatorId);
        if (type == CrossRequestResponseType.Accept && responderId != request.InitiatorId)
            OnRequestUpdated?.Invoke(responderId);

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
        r.CompletedAt ??= DateTime.UtcNow;
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
                OnRequestUpdated?.Invoke(responderId);
        }
        OnRequestUpdated?.Invoke(r.InitiatorId);
    }

    // ═══════ 超时检查 ═══════

    public void EnforceTimeouts()
    {
        var now = DateTime.UtcNow;
        foreach (var (_, request) in _requests)
        {
            if (request.ExpiresAt <= now
                && request.State == CrossRequestState.Submitted)
            {
                request.State = CrossRequestState.Timeout;
                request.CompletedAt = now;
                AppendToJournal(request);
                OnRequestUpdated?.Invoke(request.InitiatorId);
            }
        }
    }

    // ═══════ 可见性查询 ═══════

    public List<CrossRequest> GetVisible(string loopId)
    {
        return _requests.Values.Where(r =>
            r.State is CrossRequestState.Submitted
                or CrossRequestState.Accepted
                or CrossRequestState.InProgress
                or CrossRequestState.Rejected
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
                    if (req.State == CrossRequestState.InProgress)
                        req.State = CrossRequestState.Submitted;

                    latest[req.RequestId] = req;
                }
                catch { }
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
        var cutoff = DateTime.UtcNow - maxAge;
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
