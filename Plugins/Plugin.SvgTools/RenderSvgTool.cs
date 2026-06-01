using System.Drawing.Imaging;
using System.Text;
using AgentLilara.PluginSDK;
using Svg;

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
            var svgDoc = SvgDocument.Open<SvgDocument>(svgStream);
            ct.ThrowIfCancellationRequested();

            // 兜底：无尺寸时设默认值
            if (svgDoc.Width.IsEmpty || svgDoc.Width.Value <= 0)
                svgDoc.Width = 800;
            if (svgDoc.Height.IsEmpty || svgDoc.Height.Value <= 0)
                svgDoc.Height = 600;

            var width = (int)svgDoc.Width.Value;
            var height = (int)svgDoc.Height.Value;

            ct.ThrowIfCancellationRequested();

            using var bitmap = svgDoc.Draw();
            ct.ThrowIfCancellationRequested();

            var workspaceDir = _storage.WorkspaceDirectory;
            Directory.CreateDirectory(workspaceDir);
            var outputPath = Path.Combine(workspaceDir, $"{filename}.png");
            using var fileStream = File.Create(outputPath);
            bitmap.Save(fileStream, ImageFormat.Png);

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
        catch (SvgException ex)
        {
            return Task.FromResult(new ToolResult { Status = "failed", Error = $"SVG 解析失败：{ex.Message}" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult { Status = "failed", Error = $"SVG 渲染失败：{ex.Message}" });
        }
    }
}
