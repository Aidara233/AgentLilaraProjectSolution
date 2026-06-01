using AgentLilara.PluginSDK;

namespace Plugin.SvgTools;

[Component(Name = "svg-tools", Scope = ComponentScope.Global)]
public class SvgToolsComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "svg-tools",
        Description = "SVG 渲染工具",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _tools.Add(new RenderSvgTool(context.Storage));
        return Task.CompletedTask;
    }
}
