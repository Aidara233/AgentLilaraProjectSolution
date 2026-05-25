// Plugins/Plugin.SystemTools/SystemOpsComponent.cs
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

[Component(Name = "system-ops", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.NotApplicable, System = Applicability.Enabled)]
public class SystemOpsComponent : LoopComponentBase
{
    private ISubAgentAccess? _subAgents;
    private CreateSubAgentTool? _createTool;
    private StopSubAgentTool? _stopTool;
    private SendInstructionTool? _sendTool;
    private ListSubAgentsTool? _listTool;

    public override ComponentMeta Meta => new()
    {
        Name = "system-ops",
        Description = "子agent管理工具（创建、停止、追加指令、列出）",
        DefaultEnabled = true,
        PromptPriority = 50
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_createTool != null) yield return _createTool;
            if (_stopTool != null) yield return _stopTool;
            if (_sendTool != null) yield return _sendTool;
            if (_listTool != null) yield return _listTool;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _subAgents = context.GetService<ISubAgentAccess>();

        if (_subAgents != null)
        {
            _createTool = new CreateSubAgentTool(_subAgents);
            _stopTool = new StopSubAgentTool(_subAgents);
            _sendTool = new SendInstructionTool(_subAgents);
            _listTool = new ListSubAgentsTool(_subAgents);
        }

        return Task.CompletedTask;
    }
}
