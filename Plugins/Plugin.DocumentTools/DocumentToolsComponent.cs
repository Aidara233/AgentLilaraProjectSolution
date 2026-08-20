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
        var workspace = context.Workspace;
        _tools.Add(new ReadDocxTool(workspace));
        _tools.Add(new SearchDocxTool(workspace));
        _tools.Add(new WriteDocxTool(workspace));
        _tools.Add(new ReadXlsxTool(workspace));
        _tools.Add(new SearchXlsxTool(workspace));
        _tools.Add(new WriteXlsxTool(workspace));
        _tools.Add(new ReadPptxTool(workspace));
        _tools.Add(new SearchPptxTool(workspace));
        _tools.Add(new ReadPdfTool(workspace));
        _tools.Add(new SearchPdfTool(workspace));
        return Task.CompletedTask;
    }
}
