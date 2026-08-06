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
    private ListChannelsTool? _listChannels;
    private SearchChatHistoryTool? _searchChatHistory;
    private ListSessionsTool? _listSessions;
    private SendSessionMessageTool? _sendSessionMessage;

    public override ComponentMeta Meta => new()
    {
        Name = "basic-tools",
        Description = "基础通信与状态工具（speak, thinking, send_media, send_file, adapter_action, list_channels, search_chat_history, list_sessions, send_session_message）",
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
            if (_listChannels != null) yield return _listChannels;
            if (_searchChatHistory != null) yield return _searchChatHistory;
            if (_listSessions != null) yield return _listSessions;
            if (_sendSessionMessage != null) yield return _sendSessionMessage;
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
            _listChannels = new ListChannelsTool(channelAccess);
            _searchChatHistory = new SearchChatHistoryTool(channelAccess);
        }
        else
        {
            _speak = new SpeakTool();
            _sendMedia = new SendMediaTool();
            _sendFile = new SendFileTool();
            _listChannels = new ListChannelsTool();
            _searchChatHistory = new SearchChatHistoryTool();
        }
        _thinking = new ThinkingTool();

        var sessionAccess = context.GetService<ISessionAccess>();
        if (sessionAccess != null)
        {
            _listSessions = new ListSessionsTool(sessionAccess);
            _sendSessionMessage = new SendSessionMessageTool(sessionAccess, context.SessionId);
        }
        else
        {
            _listSessions = new ListSessionsTool();
            _sendSessionMessage = new SendSessionMessageTool();
        }

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
