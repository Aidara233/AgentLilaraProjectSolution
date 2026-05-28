// Plugins/Plugin.DocumentTools/DocumentToolsComponent.cs
using AgentLilara.PluginSDK;

namespace Plugin.DocumentTools;

[Component(Name = "document-tools", Scope = ComponentScope.Global)]
public class DocumentToolsComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "document-tools",
        Description = "办公文档读写：docx/xlsx/pptx/pdf 的读取、搜索、写入",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        var workspaceDir = context.Storage.WorkspaceDirectory;
        _tools.Add(new ReadDocxTool(workspaceDir));
        _tools.Add(new SearchDocxTool(workspaceDir));
        _tools.Add(new WriteDocxTool(workspaceDir));
        _tools.Add(new ReadXlsxTool(workspaceDir));
        _tools.Add(new SearchXlsxTool(workspaceDir));
        _tools.Add(new WriteXlsxTool(workspaceDir));
        _tools.Add(new ReadPptxTool(workspaceDir));
        _tools.Add(new SearchPptxTool(workspaceDir));
        _tools.Add(new ReadPdfTool(workspaceDir));
        _tools.Add(new SearchPdfTool(workspaceDir));
        return Task.CompletedTask;
    }
}
