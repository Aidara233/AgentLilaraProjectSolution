// Plugins/Plugin.BasicTools/BasicToolsComponent.cs
using AgentLilara.PluginSDK;

namespace Plugin.BasicTools;

[Component(Name = "basic-tools", Scope = ComponentScope.Global)]
public class BasicToolsComponent : GlobalComponentBase
{
    private SpeakTool? _speak;
    private SendMediaTool? _sendMedia;

    public override ComponentMeta Meta => new()
    {
        Name = "basic-tools",
        Description = "基础通信工具（speak, send_media）",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_speak != null) yield return _speak;
            if (_sendMedia != null) yield return _sendMedia;
        }
    }

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _speak = new SpeakTool();
        _sendMedia = new SendMediaTool();
        return Task.CompletedTask;
    }
}
