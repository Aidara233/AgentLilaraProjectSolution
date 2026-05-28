using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ExternalDice;

[Component(Name = "external-dice", Scope = ComponentScope.Global)]
[LoopApplicability(Channel = Applicability.NotApplicable, Review = Applicability.NotApplicable, SubAgent = Applicability.NotApplicable)]
public class ExternalDiceComponent : GlobalComponentBase
{
    public override ComponentMeta Meta => new()
    {
        Name = "external-dice",
        Description = "外部数据骰子面：适配器/人物/频道随机数据",
        DefaultEnabled = true,
        PromptPriority = 30
    };

    public override IEnumerable<ITool> Tools => Array.Empty<ITool>();

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        var diceReg = context.GetService<IDiceRegistry>();
        if (diceReg == null)
            return Task.CompletedTask;

        var adapterAccess = context.GetService<IAdapterAccess>();
        var channelAccess = context.GetService<IChannelAccess>();
        var personAccess = context.GetService<IPersonAccess>();

        ExternalDiceFaces.Register(diceReg, adapterAccess, channelAccess, personAccess);

        return Task.CompletedTask;
    }
}
