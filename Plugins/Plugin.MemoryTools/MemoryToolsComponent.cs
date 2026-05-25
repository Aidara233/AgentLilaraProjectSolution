// Plugins/Plugin.MemoryTools/MemoryToolsComponent.cs
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

[Component(Name = "memory-tools", Scope = ComponentScope.Global)]
public class MemoryToolsComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private MemoryTool? _memoryTool;

    public override ComponentMeta Meta => new()
    {
        Name = "memory-tools",
        Description = "记忆存储与检索",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_memoryTool != null) yield return _memoryTool;
        }
    }

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;
        var memoryAccess = context.GetService<IMemoryAccess>();
        if (memoryAccess != null)
            _memoryTool = new MemoryTool(memoryAccess);
        return Task.CompletedTask;
    }
}
