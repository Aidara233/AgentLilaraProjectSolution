using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ImageTools;

[ToolMeta(Group = "image", ContinueLoop = true, CapabilitySummary = "读取图片，将真实像素传入模型上下文")]
public class ReadImageTool : ITool
{
    private readonly IImageAccess _imageAccess;

    public ReadImageTool(IImageAccess imageAccess) => _imageAccess = imageAccess;

    public string Name => "read_image";
    public string Description => "读取一张图片，将图片本身传入模型上下文让模型直接\"看到\"。默认从 Workspace 目录读取，也可指定 source=received 按数据库ID读取已接收的图片。";
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

        var result = await _imageAccess.ResolveImageAsync(identifier, source);
        if (!result.Success)
            return new ToolResult { Status = "failed", Error = result.Error ?? "读取图片失败" };

        var marker = $"[IMAGE:{result.DisplayName}]";
        return new ToolResult
        {
            Status = "success",
            Data = $"图片: {result.DisplayName}\n{marker}\nId={result.ImageId}, Hash={result.ImageHash}",
            Attachments = new List<ContentAttachment>
            {
                new ContentAttachment
                {
                    Type = "image",
                    FilePath = result.LocalPath
                }
            }
        };
    }
}
