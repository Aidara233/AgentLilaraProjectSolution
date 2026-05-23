using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[Component(Name = "cross-loop", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Enabled)]
[ToolVisibility(Default = Visibility.AlwaysVisible)]
public class CrossLoopComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private IAgentMessaging? _messaging;
    private SendRequestTool? _sendRequest;
    private SendNotifyTool? _sendNotify;
    private CancelRequestTool? _cancelRequest;
    private EvaluateRequestTool? _evaluateRequest;
    private CompleteRequestTool? _completeRequest;
    private ReportProgressTool? _reportProgress;
    private CheckMessagesTool? _checkMessages;
    private RespondToRequestTool? _respondToRequest;
    private ListRequestsTool? _listRequests;
    private ListLoopsTool? _listLoops;

    public override ComponentMeta Meta => new()
    {
        Name = "cross-loop",
        Description = "跨循环委托与通信",
        DefaultEnabled = true,
        PromptPriority = 42
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_sendRequest != null) yield return _sendRequest;
            if (_sendNotify != null) yield return _sendNotify;
            if (_cancelRequest != null) yield return _cancelRequest;
            if (_evaluateRequest != null) yield return _evaluateRequest;
            if (_completeRequest != null) yield return _completeRequest;
            if (_reportProgress != null) yield return _reportProgress;
            if (_checkMessages != null) yield return _checkMessages;
            if (_respondToRequest != null) yield return _respondToRequest;
            if (_listRequests != null) yield return _listRequests;
            if (_listLoops != null) yield return _listLoops;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;
        _messaging = context.GetService<IAgentMessaging>();

        if (_messaging != null)
        {
            _sendRequest = new SendRequestTool(_messaging);
            _sendNotify = new SendNotifyTool(_messaging);
            _cancelRequest = new CancelRequestTool(_messaging);
            _evaluateRequest = new EvaluateRequestTool(_messaging);
            _completeRequest = new CompleteRequestTool(_messaging);
            _reportProgress = new ReportProgressTool(_messaging);
            _checkMessages = new CheckMessagesTool(_messaging);
            _respondToRequest = new RespondToRequestTool(_messaging);
            _listRequests = new ListRequestsTool(_messaging);
        }

        // list_loops 仅系统循环可用
        if (_ctx.LoopType == "system" && _messaging != null)
        {
            _listLoops = new ListLoopsTool(_messaging);
        }

        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        if (_messaging == null) return null;

        var active = _messaging.GetActiveRequests();
        var completed = _messaging.GetCompletedRequests();

        if (active.Count == 0 && completed.Count == 0)
            return null;

        var parts = new List<string> { "[跨循环请求状态]" };

        foreach (var req in active)
        {
            var isInitiator = _ctx.LoopId == req.InitiatorId;
            var role = isInitiator ? "我发起的" : "收到的";
            parts.Add($"- 请求#{req.Id[..8]} [{role}] {req.Title}");
            parts.Add($"  状态: {req.State} | 目标: {req.TargetId ?? "广播"}");

            if (req.Responses.Count > 0)
            {
                foreach (var resp in req.Responses)
                    parts.Add($"  {resp.Type}: {resp.Content.Truncate(60)}");
            }
        }

        foreach (var req in completed)
        {
            parts.Add($"- [已完成] 请求#{req.Id[..8]} {req.Title}");
            var lastResp = req.Responses.LastOrDefault();
            if (lastResp != null)
                parts.Add($"  结果: {lastResp.Content.Truncate(80)}");
        }

        return string.Join("\n", parts);
    }
}

internal static class StringExt
{
    public static string Truncate(this string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}
