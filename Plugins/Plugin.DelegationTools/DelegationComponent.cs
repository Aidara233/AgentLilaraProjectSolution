// Plugins/Plugin.DelegationTools/DelegationComponent.cs
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.DelegationTools;

[Component(Name = "delegation", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.NotApplicable)]
[ToolVisibility(Default = Visibility.AlwaysVisible)]
public class DelegationComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private IDelegationAccess? _delegations;
    private DelegateTaskTool? _tool;
    private CancelDelegationTool? _cancelTool;
    private readonly List<CompletedEntry> _completedBuffer = new();

    public override ComponentMeta Meta => new()
    {
        Name = "delegation",
        Description = "委托任务给系统循环处理",
        DefaultEnabled = true,
        PromptPriority = 42
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_tool != null) yield return _tool;
            if (_cancelTool != null) yield return _cancelTool;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;
        _delegations = context.GetService<IDelegationAccess>();
        if (_delegations != null)
        {
            _tool = new DelegateTaskTool(_delegations, _ctx.LoopId);
            _cancelTool = new CancelDelegationTool(_delegations);
        }
        return Task.CompletedTask;
    }

    public override Task OnActivatedAsync()
    {
        // 循环唤醒时检查已完成的委托
        CollectCompleted();
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        if (_delegations == null) return null;

        // 每次构建 prompt 时也刷新一次
        CollectCompleted();

        if (!int.TryParse(_ctx.LoopId, out var channelId))
            return null;

        var active = _delegations.GetActiveForChannel(channelId);

        if (_completedBuffer.Count == 0 && active.Count == 0)
            return null;

        var parts = new List<string> { "[委托状态]" };

        // 已完成/失败的委托结果
        if (_completedBuffer.Count > 0)
        {
            foreach (var entry in _completedBuffer)
            {
                var statusLabel = entry.Failed ? "失败" : "已完成";
                parts.Add($"- 委托#{entry.Id} ({entry.Description}): {statusLabel}");
                parts.Add($"  结果: {entry.Result}");
            }
            _completedBuffer.Clear();
        }

        // 进行中的委托
        if (active.Count > 0)
        {
            foreach (var d in active)
            {
                var statusLabel = d.Status switch
                {
                    "Accepted" => "已接受，等待执行",
                    "Queued" => "排队中",
                    "Executing" => "执行中",
                    _ => d.Status
                };
                parts.Add($"- 委托#{d.Id} ({d.Description}): {statusLabel}");
            }
        }

        return string.Join("\n", parts);
    }

    private void CollectCompleted()
    {
        if (_delegations == null) return;
        if (!int.TryParse(_ctx.LoopId, out var channelId)) return;

        var completed = _delegations.GetCompletedForChannel(channelId);
        foreach (var d in completed)
        {
            _completedBuffer.Add(new CompletedEntry(
                d.Id, d.Description, d.Result ?? "（无结果）",
                d.Status == "Failed"));
            _delegations.ConsumeCompleted(d.Id);
        }
    }

    private record CompletedEntry(string Id, string Description, string Result, bool Failed);
}
