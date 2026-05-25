// Plugins/Plugin.FileTools/FileToolsComponent.cs
using AgentLilara.PluginSDK;

namespace Plugin.FileTools;

[Component(Name = "file-tools", Scope = ComponentScope.Global)]
public class FileToolsComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "file-tools",
        Description = "文件读写操作",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _tools.Add(new ReadTextTool(context.Storage));
        _tools.Add(new WriteTextTool(context.Storage));
        _tools.Add(new ListDirTool(context.Storage));
        _tools.Add(new MoveFileTool(context.Storage));
        _tools.Add(new DeleteFileTool(context.Storage));
        _tools.Add(new CopyFileTool(context.Storage));
        return Task.CompletedTask;
    }
}
