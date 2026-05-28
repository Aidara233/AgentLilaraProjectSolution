using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ImageTools;

[Component(Name = "image-tools", Scope = ComponentScope.Global)]
public class ImageToolsComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "image-tools",
        Description = "图片读取与OCR工具",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        var imageAccess = context.GetService<IImageAccess>();
        if (imageAccess == null)
            return Task.CompletedTask;

        _tools.Add(new ReadImageTool(imageAccess));
        _tools.Add(new OcrImageTool(imageAccess));
        _tools.Add(new GetOcrTool(imageAccess));

        return Task.CompletedTask;
    }
}
