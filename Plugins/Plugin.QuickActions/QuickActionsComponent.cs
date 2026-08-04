using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.QuickActions;

[Component(Name = "quick-actions", Scope = ComponentScope.Loop)]
public class QuickActionsComponent : LoopComponentBase
{
    private PokeTool? _poke;
    private RecallTool? _recall;
    private SetGroupCardTool? _setGroupCard;

    public override ComponentMeta Meta => new()
    {
        Name = "quick-actions",
        Description = "快捷操作工具（poke, recall, set_group_card）",
        DefaultEnabled = true,
        PromptPriority = 90
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_poke != null) yield return _poke;
            if (_recall != null) yield return _recall;
            if (_setGroupCard != null) yield return _setGroupCard;
        }
    }

    public override async Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        var adapterAccess = context.GetService<IAdapterAccess>();
        var channelAccess = context.GetService<IChannelAccess>();
        var connected = context.GetService<IConnectedChannel>();
        if (adapterAccess == null || channelAccess == null) return;

        // 频道号取自连接状态（LoopId 前缀概念已移除，会话不持有 channel 号）
        var channelId = connected?.CurrentChannelId ?? 0;

        var detail = await channelAccess.GetChannelDetailAsync(channelId);
        if (detail == null) return;

        if (string.IsNullOrEmpty(detail.Name)) return;
        var adapterId = adapterAccess.GetAdapterIdForChannel(detail.Name);
        if (adapterId == null) return;

        _poke = new PokeTool(adapterAccess, adapterId);
        _recall = new RecallTool(adapterAccess, adapterId);
        _setGroupCard = new SetGroupCardTool(adapterAccess, adapterId);
    }
}
