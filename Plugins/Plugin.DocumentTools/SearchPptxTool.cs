// Plugins/Plugin.DocumentTools/SearchPptxTool.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;
using FileToolKit.Shared;

namespace Plugin.DocumentTools;

[ToolMeta(Group = "document", ContinueLoop = true, CapabilitySummary = "在 PowerPoint 幻灯片中搜索关键词")]
public class SearchPptxTool : FileToolBase
{
    private const int ContextWindow = 40;
    private const int MaxResults = 50;

    public SearchPptxTool(string workspaceDir) : base(workspaceDir) { }

    public override string Name => "search_pptx";
    public override string Description => "在 PowerPoint (.pptx) 幻灯片中搜索关键词，返回匹配页码和上下文。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("path", "文件路径（相对于 Workspace 目录）", 0),
        new("keyword", "搜索关键词", 1),
        new("max_results", "（可选）最大返回数，默认20，上限50", 2, isRequired: false)
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var keyword = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
        var maxStr = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";

        if (string.IsNullOrEmpty(path))
            return Fail("path 不能为空");
        if (string.IsNullOrEmpty(keyword))
            return Fail("keyword 不能为空");

        var fullPath = ResolvePath(path);
        if (fullPath == null)
            return Fail("路径不合法：不能访问 Workspace 目录之外的文件");
        if (!File.Exists(fullPath))
            return Fail($"文件不存在: {path}");

        var maxResults = Math.Min(int.TryParse(maxStr, out var m) && m > 0 ? m : 20, MaxResults);

        try
        {
            using var stream = File.OpenRead(fullPath);
            using var doc = PresentationDocument.Open(stream, false);
            var presPart = doc.PresentationPart;
            if (presPart == null)
                return Ok($"[{path}] 演示文稿为空，无匹配结果");

            var slideIds = presPart.Presentation.SlideIdList?.Elements<SlideId>().ToList() ?? new List<SlideId>();
            var totalMatches = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"搜索 \"{keyword}\" in [{path}]:");

            for (var i = 0; i < slideIds.Count; i++)
            {
                var slidePart = (SlidePart)presPart.GetPartById(slideIds[i].RelationshipId!);
                var pageNum = i + 1;

                foreach (var shape in slidePart.Slide.Descendants<Shape>())
                {
                    var textBody = shape.TextBody;
                    if (textBody == null) continue;

                    foreach (var para in textBody.Descendants<D.Paragraph>())
                    {
                        var paraText = ExtractParaText(para);
                        if (string.IsNullOrEmpty(paraText)) continue;

                        var pos = 0;
                        while (pos <= paraText.Length - keyword.Length)
                        {
                            var idx = paraText.IndexOf(keyword, pos, StringComparison.OrdinalIgnoreCase);
                            if (idx < 0) break;

                            totalMatches++;
                            if (totalMatches <= maxResults && sb.Length < 10000)
                            {
                                var ctxStart = Math.Max(0, idx - ContextWindow);
                                var ctxEnd = Math.Min(paraText.Length, idx + keyword.Length + ContextWindow);
                                var ctx = paraText[ctxStart..ctxEnd];
                                var prefix = ctxStart > 0 ? "..." : "";
                                var suffix = ctxEnd < paraText.Length ? "..." : "";

                                var ph = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.Elements<PlaceholderShape>().FirstOrDefault();
                                var phType = ph?.Type?.Value;
                                var elemType = phType == PlaceholderValues.Title || phType == PlaceholderValues.CenteredTitle
                                    ? "标题"
                                    : phType == PlaceholderValues.Body ? "正文" : "文本";

                                sb.AppendLine($"  第{pageNum}页 [{elemType}]: {prefix}{ctx}{suffix}");
                            }

                            pos = idx + keyword.Length;
                        }
                    }
                }
            }

            sb.Insert(sb.ToString().IndexOf(':') + 1, $" 共 {totalMatches} 处匹配");
            if (totalMatches > maxResults)
                sb.AppendLine($"\n... (仅显示前 {maxResults} 条)");

            var result = sb.ToString().TrimEnd();
            if (result.Length > 10000)
                result = result[..10000] + "\n... (结果已截断)";
            return Ok(result);
        }
        catch (Exception ex)
        {
            return Fail($"搜索失败: {ex.Message}");
        }
    }

    private static string ExtractParaText(D.Paragraph para)
    {
        var sb = new StringBuilder();
        foreach (var run in para.Descendants<D.Run>())
        {
            var text = run.Text?.Text;
            if (!string.IsNullOrEmpty(text))
                sb.Append(text);
        }
        return sb.ToString();
    }
}
