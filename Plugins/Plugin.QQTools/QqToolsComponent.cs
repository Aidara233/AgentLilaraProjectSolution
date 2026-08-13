// Plugins/Plugin.QQTools/QqToolsComponent.cs
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.QQTools;

[Component(Name = "qq-tools", Scope = ComponentScope.Loop)]
public class QqToolsComponent : LoopComponentBase
{
    private GetForwardMsgTool? _forward;

    public override ComponentMeta Meta => new()
    {
        Name = "qq-tools",
        Description = "QQ 合并转发查询（get_forward_msg）",
        DefaultEnabled = true,
        PromptPriority = 80
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_forward != null) yield return _forward;
        }
    }

    public override async Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        var adapterAccess = context.GetService<IAdapterAccess>();
        if (adapterAccess == null) return;

        // task/review 等会话可能没有频道连接，按平台解析 qq 适配器
        var adapterId = adapterAccess.GetAdapterIdByPlatform("qq");
        if (adapterId == null) return;

        var downloadsDir = Path.Combine(context.Storage.WorkspaceDirectory, "Downloads", "Forward");
        _forward = new GetForwardMsgTool(adapterAccess, adapterId, downloadsDir);
    }
}
