// Plugins/Plugin.DocumentTools/ReadPptxTool.cs
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

[ToolMeta(Group = "document", ContinueLoop = true, CapabilitySummary = "读取 PowerPoint 幻灯片内容")]
public class ReadPptxTool : FileToolBase
{
    private const int MaxOutputLen = 10000;

    public ReadPptxTool(string workspaceDir) : base(workspaceDir) { }

    public override string Name => "read_pptx";
    public override string Description => "读取 PowerPoint (.pptx) 幻灯片。不提供全文提取——先不带 slides 参数调用获取页数和标题列表，再按需指定 slides 读取特定页。slides 格式如 \"1-5\" / \"1,3,7\"。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("path", "文件路径（相对于 Workspace 目录）", 0),
        new("slides", "（可选）页码范围，如 \"1-5\" / \"1,3,7\" / \"3-\"。1-based", 1, isRequired: false),
        new("include_notes", "（可选）是否包含备注，true/false，默认 false", 2, isRequired: false)
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var slides = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
        var includeNotes = resolvedInputs.Count > 2 && resolvedInputs[2].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(path))
            return Fail("path 不能为空");

        var fullPath = ResolvePath(path);
        if (fullPath == null)
            return Fail("路径不合法：不能访问 Workspace 目录之外的文件");
        if (!File.Exists(fullPath))
            return Fail($"文件不存在: {path}");

        try
        {
            using var stream = File.OpenRead(fullPath);
            using var doc = PresentationDocument.Open(stream, false);
            var presPart = doc.PresentationPart;
            if (presPart == null)
                return Fail("文档为空或格式损坏");

            var slideIds = presPart.Presentation.SlideIdList?.Elements<SlideId>().ToList() ?? new List<SlideId>();
            var totalPages = slideIds.Count;
            if (totalPages == 0)
                return Ok($"[{path}] 演示文稿为空，无幻灯片");

            // 提取所有页的标题
            var pageTitles = new List<string>();
            foreach (var slideId in slideIds)
            {
                var slidePart = (SlidePart)presPart.GetPartById(slideId.RelationshipId!);
                var title = ExtractSlideTitle(slidePart);
                pageTitles.Add(string.IsNullOrEmpty(title) ? "(无标题)" : title);
            }

            if (string.IsNullOrEmpty(slides))
            {
                // 元数据模式
                var sb = new StringBuilder();
                sb.AppendLine($"[{path}] 共 {totalPages} 页:");
                for (var i = 0; i < totalPages; i++)
                    sb.AppendLine($"  [{i + 1}] {pageTitles[i]}");

                return Ok(sb.ToString().TrimEnd());
            }

            // 范围读取模式
            var indices = DocumentRangeParser.Parse(slides, totalPages);
            if (indices.Count == 0)
                return Fail($"slides 参数格式错误: {slides}");

            var sb2 = new StringBuilder();
            sb2.AppendLine($"[{path}] 共 {totalPages} 页，读取 {indices.Count} 页");

            foreach (var pageNum in indices)
            {
                var slideId = slideIds[pageNum - 1];
                var slidePart = (SlidePart)presPart.GetPartById(slideId.RelationshipId!);

                sb2.AppendLine();
                sb2.AppendLine($"=== 第 {pageNum} 页: {pageTitles[pageNum - 1]} ===");

                var texts = ExtractSlideTexts(slidePart);
                foreach (var (text, indent) in texts)
                {
                    var prefix = indent > 0 ? new string(' ', indent * 2) : "";
                    sb2.AppendLine($"{prefix}{text}");
                }

                if (includeNotes)
                {
                    var notes = ExtractNotes(slidePart);
                    if (!string.IsNullOrEmpty(notes))
                        sb2.AppendLine($"  [备注] {notes}");
                }
            }

            var result2 = sb2.ToString().TrimEnd();
            if (result2.Length > MaxOutputLen)
                result2 = DocumentRangeParser.Truncate(result2, MaxOutputLen, "页", totalPages);
            return Ok(result2);
        }
        catch (Exception ex)
        {
            return Fail($"读取失败: {ex.Message}");
        }
    }

    internal static string ExtractSlideTitle(SlidePart slidePart)
    {
        var shapes = slidePart.Slide.Descendants<Shape>().ToList();

        // 标题通常是第一个 placeholder type 为 Title 的形状
        foreach (var shape in shapes)
        {
            var ph = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.Elements<PlaceholderShape>().FirstOrDefault();
            if (ph != null && (ph.Type?.Value == PlaceholderValues.Title || ph.Type?.Value == PlaceholderValues.CenteredTitle))
                return ExtractTextFromShape(shape);
        }

        // fallback: 返回第一个非空文本
        foreach (var shape in shapes)
        {
            var text = ExtractTextFromShape(shape);
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }
        return "";
    }

    internal static List<(string Text, int Indent)> ExtractSlideTexts(SlidePart slidePart)
    {
        var results = new List<(string, int)>();
        var shapes = slidePart.Slide.Descendants<Shape>().ToList();

        foreach (var shape in shapes)
        {
            var textBody = shape.TextBody;
            if (textBody == null) continue;

            foreach (var para in textBody.Descendants<D.Paragraph>())
            {
                var text = ExtractParagraphText(para);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var indent = GetParagraphIndent(para);
                results.Add((text.Trim(), indent));
            }
        }
        return results;
    }

    private static string ExtractTextFromShape(Shape shape)
    {
        var sb = new StringBuilder();
        var textBody = shape.TextBody;
        if (textBody == null) return "";

        foreach (var para in textBody.Descendants<D.Paragraph>())
        {
            var text = ExtractParagraphText(para);
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(text.Trim());
            }
        }
        return sb.ToString();
    }

    private static string ExtractParagraphText(D.Paragraph para)
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

    private static int GetParagraphIndent(D.Paragraph para)
    {
        var props = para.ParagraphProperties;
        if (props?.Level?.Value is int level)
            return level;
        return 0;
    }

    private static string ExtractNotes(SlidePart slidePart)
    {
        var notesPart = slidePart.NotesSlidePart;
        if (notesPart == null) return "";

        var sb = new StringBuilder();
        foreach (var shape in notesPart.NotesSlide.Descendants<Shape>())
        {
            var text = ExtractTextFromShape(shape);
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(text.Trim());
            }
        }
        return sb.ToString();
    }
}
