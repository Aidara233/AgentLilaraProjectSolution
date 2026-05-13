// Plugins/Plugin.SystemTools/SystemOpsComponent.cs
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

[Component(Name = "system-ops", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.NotApplicable, System = Applicability.Enabled)]
[ToolVisibility(Default = Visibility.AlwaysVisible)]
public class SystemOpsComponent : LoopComponentBase
{
    private IDelegationAccess? _delegations;
    private IChannelAccess? _channels;
    private ISubAgentAccess? _subAgents;

    private EvaluateDelegationTool? _evalTool;
    private NotifyChannelTool? _notifyTool;
    private CreateSubAgentTool? _createTool;
    private StopSubAgentTool? _stopTool;

    public override ComponentMeta Meta => new()
    {
        Name = "system-ops",
        Description = "系统循环操作工具集（委托评估、频道通知、子agent管理）",
        DefaultEnabled = true,
        PromptPriority = 50
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_evalTool != null) yield return _evalTool;
            if (_notifyTool != null) yield return _notifyTool;
            if (_createTool != null) yield return _createTool;
            if (_stopTool != null) yield return _stopTool;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _delegations = context.GetService<IDelegationAccess>();
        _channels = context.GetService<IChannelAccess>();
        _subAgents = context.GetService<ISubAgentAccess>();

        if (_delegations != null)
            _evalTool = new EvaluateDelegationTool(_delegations);
        if (_channels != null)
            _notifyTool = new NotifyChannelTool(_channels);
        if (_subAgents != null)
        {
            _createTool = new CreateSubAgentTool(_subAgents);
            _stopTool = new StopSubAgentTool(_subAgents);
        }

        return Task.CompletedTask;
    }
}
