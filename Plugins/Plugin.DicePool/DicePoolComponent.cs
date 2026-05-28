using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.DicePool;

[Component(Name = "dice-pool", Scope = ComponentScope.Global)]
[LoopApplicability(Channel = Applicability.NotApplicable, Review = Applicability.NotApplicable, SubAgent = Applicability.NotApplicable)]
public class DicePoolComponent : GlobalComponentBase
{
    private RollDiceTool? _tool;

    public override ComponentMeta Meta => new()
    {
        Name = "dice-pool",
        Description = "骰子系统：随机碎片碰撞灵感",
        DefaultEnabled = true,
        PromptPriority = 50
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_tool != null) yield return _tool;
        }
    }

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        var diceService = context.GetService<IDiceService>();
        _tool = new RollDiceTool(diceService);
        return Task.CompletedTask;
    }
}
