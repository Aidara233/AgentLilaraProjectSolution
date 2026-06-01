using System.Text;
using AgentLilara.PluginSDK;
using SkiaSharp;
using Svg.Skia;

namespace Plugin.SvgTools;

[ToolMeta(Group = "svg", ContinueLoop = true, CapabilitySummary = "将 SVG 代码渲染为 PNG 图片")]
public class RenderSvgTool : ITool
{
    private readonly IPluginStorage _storage;

    public RenderSvgTool(IPluginStorage storage) => _storage = storage;

    public string Name => "render_svg";
    public string Description => "将 SVG 代码渲染为 PNG 图片并保存到 workspace。输入完整的 SVG 源码，返回渲染后的图片。";
    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("svg_code", "完整的 SVG 源代码", 0),
        new ToolParameter("filename", "输出文件名（不含扩展名），默认 svg_render", 1, isRequired: false)
    };
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var svgCode = resolvedInputs.ElementAtOrDefault(0)?.Trim() ?? "";
        var filename = resolvedInputs.ElementAtOrDefault(1)?.Trim();
        if (string.IsNullOrEmpty(filename))
            filename = "svg_render";

        if (string.IsNullOrEmpty(svgCode))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "svg_code 不能为空" });

        ct.ThrowIfCancellationRequested();

        try
        {
            using var svgStream = new MemoryStream(Encoding.UTF8.GetBytes(svgCode));
            var svg = new SKSvg();
            svg.Load(svgStream);
            ct.ThrowIfCancellationRequested();

            if (svg.Picture == null)
                return Task.FromResult(new ToolResult { Status = "failed", Error = "SVG 解析失败：无法渲染" });

            var width = (int)Math.Ceiling(svg.Picture.CullRect.Width);
            var height = (int)Math.Ceiling(svg.Picture.CullRect.Height);

            if (width <= 0 || height <= 0)
                return Task.FromResult(new ToolResult { Status = "failed", Error = $"SVG 尺寸无效：{width}x{height}" });

            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawPicture(svg.Picture);
            canvas.Flush();
            ct.ThrowIfCancellationRequested();

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            ct.ThrowIfCancellationRequested();

            var workspaceDir = _storage.WorkspaceDirectory;
            Directory.CreateDirectory(workspaceDir);
            var outputPath = Path.Combine(workspaceDir, $"{filename}.png");
            using var fileStream = File.Create(outputPath);
            data.SaveTo(fileStream);

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = $"SVG 已渲染为图片 ({width}x{height})，保存到 workspace/{filename}.png，图片紧随其后",
                Attachments = new List<ContentAttachment>
                {
                    new() { Type = "image", FilePath = outputPath }
                }
            });
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new ToolResult { Status = "cancelled", Error = "渲染已取消" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult { Status = "failed", Error = $"SVG 渲染失败：{ex.Message}" });
        }
    }
}
