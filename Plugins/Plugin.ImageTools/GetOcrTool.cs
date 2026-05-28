using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ImageTools;

[ToolMeta(Group = "image", ContinueLoop = true, CapabilitySummary = "获取图片的缓存OCR结果")]
public class GetOcrTool : ITool
{
    private readonly IImageAccess _imageAccess;

    public GetOcrTool(IImageAccess imageAccess) => _imageAccess = imageAccess;

    public string Name => "get_ocr";
    public string Description => "获取图片的缓存 OCR 文字识别结果。不会调用 API，仅返回之前处理过的结果。若尚未处理，请先使用 ocr_image。";
    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("image", "图片标识：workspace相对路径或received图片的数据库ID", 0),
        new ToolParameter("source", "可选，workspace（默认）或 received", 1, isRequired: false)
    };
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var identifier = resolvedInputs.ElementAtOrDefault(0)?.Trim() ?? "";
        var source = (resolvedInputs.ElementAtOrDefault(1)?.Trim()?.ToLowerInvariant()) switch
        {
            "received" => "received",
            _ => "workspace"
        };

        if (string.IsNullOrEmpty(identifier))
            return new ToolResult { Status = "failed", Error = "image 参数不能为空" };

        var result = await _imageAccess.GetOcrAsync(identifier, source);
        if (!result.Success)
            return new ToolResult { Status = "failed", Error = result.Error ?? "获取OCR失败" };

        if (result.HasText)
            return new ToolResult { Status = "success", Data = result.Text! };

        return new ToolResult { Status = "success", Data = result.Text ?? "(无缓存OCR结果)" };
    }
}
