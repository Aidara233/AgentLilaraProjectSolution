using AgentLilara.PluginSDK.Services;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Component;

internal class DelegationAccessAdapter : IDelegationAccess
{
    private readonly DelegationRegistry _registry;

    public DelegationAccessAdapter(DelegationRegistry registry)
    {
        _registry = registry;
    }

    public async Task<DelegationSubmitResult> SubmitAndWaitAsync(
        string description, string? context, int channelId, int personId, TimeSpan timeout)
    {
        var delegation = new Delegation
        {
            DelegationId = Guid.NewGuid().ToString("N")[..12],
            Description = description,
            ContextSummary = context ?? "",
            SourceChannelId = channelId,
            RequestingPersonId = personId
        };

        _registry.Submit(delegation);
        var evaluation = await _registry.WaitForEvaluationAsync(delegation.DelegationId, timeout);

        if (evaluation == null)
            return new DelegationSubmitResult { Success = false, TimedOut = true, Verdict = "timeout", Reason = "评估超时" };

        return new DelegationSubmitResult
        {
            Success = true,
            Verdict = evaluation.Verdict.ToString().ToLower(),
            Reason = evaluation.Reason
        };
    }

    public bool ResolveEvaluation(string delegationId, string verdict, string reason)
    {
        var status = verdict.ToLower() switch
        {
            "accept" => DelegationStatus.Accepted,
            "queue" => DelegationStatus.Queued,
            "reject" => DelegationStatus.Rejected,
            _ => DelegationStatus.Rejected
        };

        return _registry.ResolveEvaluation(delegationId, new DelegationEvaluation
        {
            Verdict = status,
            Reason = reason
        });
    }

    public List<DelegationInfo> GetPendingForEvaluation()
    {
        return _registry.GetPendingForEvaluation()
            .Select(ToInfo)
            .ToList();
    }

    public void MarkExecuting(string delegationId) => _registry.MarkExecuting(delegationId);
    public void MarkCompleted(string delegationId, string result) => _registry.MarkCompleted(delegationId, result);
    public void MarkFailed(string delegationId, string error) => _registry.MarkFailed(delegationId, error);

    public DelegationInfo? Get(string delegationId)
    {
        var d = _registry.Get(delegationId);
        return d != null ? ToInfo(d) : null;
    }

    public List<DelegationInfo> GetCompletedForChannel(int channelId)
    {
        return _registry.GetCompletedForChannel(channelId)
            .Select(ToInfo)
            .ToList();
    }

    public List<DelegationInfo> GetActiveForChannel(int channelId)
    {
        return _registry.GetActiveForChannel(channelId)
            .Select(ToInfo)
            .ToList();
    }

    public void ConsumeCompleted(string delegationId) => _registry.ConsumeCompleted(delegationId);

    public bool Cancel(string delegationId) => _registry.Cancel(delegationId);

    private static DelegationInfo ToInfo(Delegation d) => new()
    {
        Id = d.DelegationId,
        Description = d.Description,
        Context = d.ContextSummary,
        ChannelId = d.SourceChannelId,
        Status = d.Status.ToString(),
        Result = d.Result
    };
}
