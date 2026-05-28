// Plugins/Plugin.MemoryTools/MemoryToolsComponent.cs
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

[Component(Name = "memory-tools", Scope = ComponentScope.Global)]
public class MemoryToolsComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "memory-tools",
        Description = "记忆存储、检索、筛选、关联与统计",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;
        var memoryAccess = context.GetService<IMemoryAccess>();

        _tools.Add(new MemoryStoreTool(memoryAccess));
        _tools.Add(new MemoryGetTool(memoryAccess));
        _tools.Add(new MemoryUpdateTool(memoryAccess));
        _tools.Add(new MemoryDeleteTool(memoryAccess));
        _tools.Add(new MemorySearchTool(memoryAccess));
        _tools.Add(new MemoryListTool(memoryAccess));
        _tools.Add(new MemoryLinkCreateTool(memoryAccess));
        _tools.Add(new MemoryLinkDeleteTool(memoryAccess));
        _tools.Add(new MemoryLinkGetTool(memoryAccess));
        _tools.Add(new MemoryStatsTool(memoryAccess));

        // 注册骰子面
        var diceReg = context.GetService<IDiceRegistry>();
        if (diceReg != null && memoryAccess != null)
            MemoryDiceFaces.Register(diceReg, memoryAccess);

        return Task.CompletedTask;
    }
}
