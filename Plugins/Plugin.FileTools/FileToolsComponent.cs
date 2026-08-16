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
        var workspace = context.Workspace;
        _tools.Add(new ReadTextTool(workspace));
        _tools.Add(new WriteTextTool(workspace));
        _tools.Add(new ListDirTool(workspace));
        _tools.Add(new MoveFileTool(workspace));
        _tools.Add(new DeleteFileTool(workspace));
        _tools.Add(new CopyFileTool(workspace));
        _tools.Add(new UpdateTextTool(workspace));

        return Task.CompletedTask;
    }
}
