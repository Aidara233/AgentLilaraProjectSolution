using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ImageTools;

[ToolMeta(Group = "image", ContinueLoop = true, CapabilitySummary = "读取/描述图片内容")]
public class ReadImageTool : ITool
{
    private readonly IImageAccess _imageAccess;

    public ReadImageTool(IImageAccess imageAccess) => _imageAccess = imageAccess;

    public string Name => "read_image";
    public string Description => "读取并描述图片内容。默认从 Workspace 目录读取文件，也可指定 source=received 按数据库ID读取已接收的图片。返回视觉描述文本。";
    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("image", "图片标识：workspace相对路径或received图片的数据库ID", 0),
        new ToolParameter("source", "可选，workspace（默认）或 received", 1, isRequired: false),
        new ToolParameter("context_hint", "可选，帮助视觉模型聚焦描述的上下文提示，如\"关注图中文字\"", 2, isRequired: false)
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
        var contextHint = resolvedInputs.ElementAtOrDefault(2)?.Trim();
        if (string.IsNullOrEmpty(contextHint)) contextHint = null;

        if (string.IsNullOrEmpty(identifier))
            return new ToolResult { Status = "failed", Error = "image 参数不能为空" };

        var result = await _imageAccess.ReadImageAsync(identifier, source, contextHint: contextHint);
        if (!result.Success)
            return new ToolResult { Status = "failed", Error = result.Error ?? "读取图片失败" };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(result.Description ?? "(无描述)");
        sb.Append($"ImageId={result.ImageId}, Hash={result.ImageHash}, Category={result.Category ?? "null"}, Cached={result.WasCached}");
        return new ToolResult { Status = "success", Data = sb.ToString() };
    }
}
