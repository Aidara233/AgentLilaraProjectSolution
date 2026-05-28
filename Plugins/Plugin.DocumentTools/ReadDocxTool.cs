// Plugins/Plugin.DocumentTools/ReadDocxTool.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FileToolKit.Shared;

namespace Plugin.DocumentTools;

[ToolMeta(Group = "document", ContinueLoop = true, CapabilitySummary = "读取 Word 文档内容")]
public class ReadDocxTool : FileToolBase
{
    private const int MaxOutputLen = 10000;
    private const int PreviewParagraphs = 5;

    public ReadDocxTool(string workspaceDir) : base(workspaceDir) { }

    public override string Name => "read_docx";
    public override string Description => "读取 Word (.docx) 文档。不提供全文提取——先不带 range 参数调用获取元数据和预览，再按需指定 range 读取特定段落。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("path", "文件路径（相对于 Workspace 目录）", 0),
        new("range", "（可选）段落范围，如 \"1-10\" / \"1,3,5\" / \"5-\"。1-based，从第1段开始", 1, isRequired: false)
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var range = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

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
            using var doc = WordprocessingDocument.Open(stream, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null)
                return Fail("文档为空或格式损坏");

            var paragraphs = body.Elements<Paragraph>().ToList();
            var totalParagraphs = paragraphs.Count;

            // 提取元数据
            var meta = ExtractMetadata(doc, paragraphs);

            if (string.IsNullOrEmpty(range))
            {
                // 元数据 + 预览模式
                var sb = new StringBuilder();
                AppendMetadata(sb, meta);
                sb.AppendLine();
                sb.AppendLine($"--- 预览（前 {PreviewParagraphs} 段）---");

                var preview = paragraphs.Take(PreviewParagraphs).ToList();
                for (var i = 0; i < preview.Count; i++)
                {
                    var p = preview[i];
                    var idx = i + 1;
                    var text = ExtractParagraphText(p);
                    var heading = GetHeadingLevel(p);
                    var prefix = heading > 0 ? $"{new string('#', heading)} " : "";
                    sb.AppendLine($"[{idx}] {prefix}{text}");
                }

                var result = sb.ToString().TrimEnd();
                if (result.Length > MaxOutputLen)
                    result = DocumentRangeParser.Truncate(result, MaxOutputLen, "段", totalParagraphs);
                return Ok(result);
            }

            // 范围读取模式
            var indices = DocumentRangeParser.Parse(range, totalParagraphs);
            if (indices.Count == 0)
                return Fail($"range 参数格式错误: {range}");

            var sb2 = new StringBuilder();
            sb2.AppendLine($"[{path}] 共 {totalParagraphs} 段，读取 {indices.Count} 段");

            foreach (var idx in indices)
            {
                var p = paragraphs[idx - 1];
                var text = ExtractParagraphText(p);
                var heading = GetHeadingLevel(p);
                var prefix = heading > 0 ? $"{new string('#', heading)} " : "";
                sb2.AppendLine($"[{idx}] {prefix}{text}");
            }

            var result2 = sb2.ToString().TrimEnd();
            if (result2.Length > MaxOutputLen)
                result2 = DocumentRangeParser.Truncate(result2, MaxOutputLen, "段", totalParagraphs);
            return Ok(result2);
        }
        catch (Exception ex)
        {
            return Fail($"读取失败: {ex.Message}");
        }
    }

    private static (string Title, string? Author, int WordCount, int ParagraphCount) ExtractMetadata(
        WordprocessingDocument doc, List<Paragraph> paragraphs)
    {
        var props = doc.PackageProperties;
        var title = props.Title;
        var author = props.Creator;

        // 尝试从第一个标题段落获取标题
        if (string.IsNullOrEmpty(title))
        {
            var firstHeading = paragraphs.FirstOrDefault(p => GetHeadingLevel(p) > 0);
            if (firstHeading != null)
                title = ExtractParagraphText(firstHeading);
        }

        var wordCount = 0;
        foreach (var p in paragraphs)
        {
            var text = ExtractParagraphText(p);
            wordCount += text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        return (title ?? "", author, wordCount, paragraphs.Count);
    }

    private static void AppendMetadata(StringBuilder sb, (string Title, string? Author, int WordCount, int ParagraphCount) meta)
    {
        sb.AppendLine($"标题: {meta.Title}");
        if (!string.IsNullOrEmpty(meta.Author))
            sb.AppendLine($"作者: {meta.Author}");
        sb.AppendLine($"字数: {meta.WordCount:N0}");
        sb.AppendLine($"段落数: {meta.ParagraphCount}");
    }

    internal static string ExtractParagraphText(Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var run in p.Elements<Run>())
        {
            foreach (var text in run.Elements<Text>())
                sb.Append(text.Text);
        }
        return sb.ToString().Trim();
    }

    internal static int GetHeadingLevel(Paragraph p)
    {
        var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
        if (styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
        {
            var numStr = styleId["Heading".Length..];
            if (int.TryParse(numStr, out var level))
                return level;
        }
        return 0;
    }
}
