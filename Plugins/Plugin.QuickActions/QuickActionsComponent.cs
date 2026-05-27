using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.QuickActions;

[Component(Name = "quick-actions", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Disabled)]
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
        if (adapterAccess == null || channelAccess == null) return;

        var loopId = context.LoopId;
        var colonIdx = loopId.LastIndexOf(':');
        if (colonIdx < 0 || !int.TryParse(loopId[(colonIdx + 1)..], out var channelId))
            channelId = 0;

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
