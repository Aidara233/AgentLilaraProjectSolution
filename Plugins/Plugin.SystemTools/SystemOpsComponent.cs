// Plugins/Plugin.SystemTools/SystemOpsComponent.cs
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

[Component(Name = "system-ops", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.NotApplicable, System = Applicability.Enabled)]
[ToolVisibility(Default = Visibility.AlwaysVisible)]
public class SystemOpsComponent : LoopComponentBase
{
    private ISubAgentAccess? _subAgents;
    private CreateSubAgentTool? _createTool;
    private StopSubAgentTool? _stopTool;

    public override ComponentMeta Meta => new()
    {
        Name = "system-ops",
        Description = "子agent管理工具（创建、停止）",
        DefaultEnabled = true,
        PromptPriority = 50
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_createTool != null) yield return _createTool;
            if (_stopTool != null) yield return _stopTool;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _subAgents = context.GetService<ISubAgentAccess>();

        if (_subAgents != null)
        {
            _createTool = new CreateSubAgentTool(_subAgents);
            _stopTool = new StopSubAgentTool(_subAgents);
        }

        return Task.CompletedTask;
    }
}
