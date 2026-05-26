// Plugins/Plugin.BasicTools/BasicToolsComponent.cs
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.BasicTools;

[Component(Name = "basic-tools", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Disabled)]
public class BasicToolsComponent : LoopComponentBase
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

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        var channelAccess = context.GetService<IChannelAccess>();
        var channelId = ParseChannelId(context.LoopId);

        _speak = channelAccess != null
            ? new SpeakTool(channelAccess, channelId)
            : new SpeakTool();
        _sendMedia = channelAccess != null
            ? new SendMediaTool(channelAccess, channelId)
            : new SendMediaTool();
        return Task.CompletedTask;
    }

    private static int ParseChannelId(string loopId)
    {
        // loopId 格式: "channel:123"
        var colonIndex = loopId.LastIndexOf(':');
        if (colonIndex >= 0 && int.TryParse(loopId[(colonIndex + 1)..], out var id))
            return id;
        return 0;
    }
}
