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
    private SendFileTool? _sendFile;
    private AdapterActionTool? _adapterAction;

    public override ComponentMeta Meta => new()
    {
        Name = "basic-tools",
        Description = "基础通信工具（speak, send_media, send_file, adapter_action）",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_speak != null) yield return _speak;
            if (_sendMedia != null) yield return _sendMedia;
            if (_sendFile != null) yield return _sendFile;
            if (_adapterAction != null) yield return _adapterAction;
        }
    }

    public override async Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        var channelAccess = context.GetService<IChannelAccess>();
        var channelId = ParseChannelId(context.LoopId);
        var speakGuard = context.GetService<ISpeakGuard>();

        _speak = channelAccess != null
            ? new SpeakTool(channelAccess, channelId, speakGuard)
            : new SpeakTool();
        _sendMedia = channelAccess != null
            ? new SendMediaTool(channelAccess, channelId)
            : new SendMediaTool();
        _sendFile = channelAccess != null
            ? new SendFileTool(channelAccess, channelId)
            : new SendFileTool();

        var adapterAccess = context.GetService<IAdapterAccess>();
        if (adapterAccess != null && channelAccess != null)
        {
            var detail = await channelAccess.GetChannelDetailAsync(channelId);
            if (detail != null && !string.IsNullOrEmpty(detail.Name))
            {
                var adapterId = adapterAccess.GetAdapterIdForChannel(detail.Name);
                if (adapterId != null)
                    _adapterAction = new AdapterActionTool(adapterAccess, adapterId);
            }
        }

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
