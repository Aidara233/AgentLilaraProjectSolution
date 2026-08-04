// Plugins/Plugin.BasicTools/BasicToolsComponent.cs
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.BasicTools;

[Component(Name = "basic-tools", Scope = ComponentScope.Loop)]
public class BasicToolsComponent : LoopComponentBase
{
    private SpeakTool? _speak;
    private ThinkingTool? _thinking;
    private SendMediaTool? _sendMedia;
    private SendFileTool? _sendFile;
    private AdapterActionTool? _adapterAction;

    public override ComponentMeta Meta => new()
    {
        Name = "basic-tools",
        Description = "基础通信工具（speak, thinking, send_media, send_file, adapter_action）",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_speak != null) yield return _speak;
            if (_thinking != null) yield return _thinking;
            if (_sendMedia != null) yield return _sendMedia;
            if (_sendFile != null) yield return _sendFile;
            if (_adapterAction != null) yield return _adapterAction;
        }
    }

    public override async Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        var channelAccess = context.GetService<IChannelAccess>();
        var speakGuard = context.GetService<ISpeakGuard>();
        // 连接驱动：session 注入 IConnectedChannel，发送工具据此运行期决定目标频道并校验发言权限。
        // 所有会话（express/working/task）都注入 IConnectedChannel，频道号取自 connected.CurrentChannelId。
        var connected = context.GetService<IConnectedChannel>();
        var channelId = connected?.CurrentChannelId ?? 0;

        if (channelAccess != null)
        {
            _speak = connected != null
                ? new SpeakTool(channelAccess, connected, speakGuard)
                : new SpeakTool(channelAccess, channelId, speakGuard);
            _sendMedia = connected != null
                ? new SendMediaTool(channelAccess, connected)
                : new SendMediaTool(channelAccess, channelId);
            _sendFile = connected != null
                ? new SendFileTool(channelAccess, connected)
                : new SendFileTool(channelAccess, channelId);
        }
        else
        {
            _speak = new SpeakTool();
            _sendMedia = new SendMediaTool();
            _sendFile = new SendFileTool();
        }
        _thinking = new ThinkingTool();

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
}
