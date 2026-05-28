using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ImageTools;

[ToolMeta(Group = "image", ContinueLoop = true, CapabilitySummary = "对图片执行OCR文字识别")]
public class OcrImageTool : ITool
{
    private readonly IImageAccess _imageAccess;

    public OcrImageTool(IImageAccess imageAccess) => _imageAccess = imageAccess;

    public string Name => "ocr_image";
    public string Description => "对图片执行 OCR 文字识别。始终调用 API（忽略缓存结果），结果会自动保存到数据库。";
    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("image", "图片标识：workspace相对路径或received图片的数据库ID", 0),
        new ToolParameter("source", "可选，workspace（默认）或 received", 1, isRequired: false)
    };
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

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

        var result = await _imageAccess.OcrImageAsync(identifier, source);
        if (!result.Success)
            return new ToolResult { Status = "failed", Error = result.Error ?? "OCR处理失败" };

        if (result.HasText)
            return new ToolResult { Status = "success", Data = result.Text ?? "(OCR检测到文字但内容为空)" };

        return new ToolResult { Status = "success", Data = "(图片中未检测到文字)" };
    }
}
