// Plugins/Plugin.FileOps/FileOpsComponent.cs
using AgentLilara.PluginSDK;

namespace Plugin.FileOps;

[Component(Name = "file-ops", Scope = ComponentScope.Global)]
public class FileOpsComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "file-ops",
        Description = "高级文件操作：压缩归档、搜索、元数据、对比",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        var workspaceDir = context.Storage.WorkspaceDirectory;
        _tools.Add(new ArchiveCreateTool(workspaceDir));
        _tools.Add(new ArchiveExtractTool(workspaceDir));
        _tools.Add(new ArchiveListTool(workspaceDir));
        _tools.Add(new SearchFilesTool(workspaceDir));
        _tools.Add(new GrepFilesTool(workspaceDir));
        return Task.CompletedTask;
    }
}
