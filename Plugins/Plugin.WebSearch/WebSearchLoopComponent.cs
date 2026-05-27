using AgentLilara.PluginSDK;

namespace Plugin.WebSearch;

[Component(Name = "web-search", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Enabled,
    SubAgent = Applicability.Enabled, Review = Applicability.Disabled)]
public class WebSearchLoopComponent : LoopComponentBase
{
    private WebSearchTool? _search;

    public override ComponentMeta Meta => new()
    {
        Name = "web-search",
        Description = "网页搜索工具",
        DefaultEnabled = true,
        PromptPriority = 90
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_search != null) yield return _search;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        var backend = WebSearchAccessor.Backend
            ?? throw new InvalidOperationException("WebSearchGlobalComponent 未初始化");

        _search = new WebSearchTool(backend);
        return Task.CompletedTask;
    }
}
