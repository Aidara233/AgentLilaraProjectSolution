// Plugins/Plugin.DocumentTools/ReadPdfTool.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using FileToolKit.Shared;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace Plugin.DocumentTools;

[ToolMeta(Group = "document", ContinueLoop = true, CapabilitySummary = "读取 PDF 文档内容")]
public class ReadPdfTool : FileToolBase
{
    private const int MaxOutputLen = 10000;
    private const int PreviewPages = 2;

    public ReadPdfTool(string workspaceDir) : base(workspaceDir) { }

    public override string Name => "read_pdf";
    public override string Description => "读取 PDF 文档。不提供全文提取——先不带 pages 参数调用获取总页数和目录，再按需指定 pages 读取特定页。pages 格式如 \"1-10\" / \"1,3,5\"。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("path", "文件路径（相对于 Workspace 目录）", 0),
        new("pages", "（可选）页码范围，如 \"1-10\" / \"1,3,5\" / \"5-\"。1-based", 1, isRequired: false)
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(20);

    public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var pages = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

        if (string.IsNullOrEmpty(path))
            return Fail("path 不能为空");

        var fullPath = ResolvePath(path);
        if (fullPath == null)
            return Fail("路径不合法：不能访问 Workspace 目录之外的文件");
        if (!File.Exists(fullPath))
            return Fail($"文件不存在: {path}");

        try
        {
            using var doc = PdfDocument.Open(fullPath);
            var totalPages = doc.NumberOfPages;

            if (string.IsNullOrEmpty(pages))
            {
                // 元数据 + 目录 + 预览模式
                var sb = new StringBuilder();
                sb.AppendLine($"[{path}] 共 {totalPages} 页");

                // 目录（书签）
                if (doc.TryGetBookmarks(out var bookmarks) && bookmarks.Roots.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("--- 目录 ---");
                    AppendBookmarks(sb, bookmarks.Roots, 0);
                }

                sb.AppendLine();
                sb.AppendLine($"--- 预览（前 {PreviewPages} 页）---");
                for (var i = 1; i <= Math.Min(PreviewPages, totalPages); i++)
                {
                    var pageText = ExtractPageText(doc, i);
                    sb.AppendLine($"[Page {i}]");
                    sb.AppendLine(pageText.Length > 500 ? pageText[..500] + "..." : pageText);
                    sb.AppendLine();
                }

                var result = sb.ToString().TrimEnd();
                if (result.Length > MaxOutputLen)
                    result = result[..MaxOutputLen] + "\n... (结果已截断)";
                return Ok(result);
            }

            // 范围读取模式
            var indices = DocumentRangeParser.Parse(pages, totalPages);
            if (indices.Count == 0)
                return Fail($"pages 参数格式错误: {pages}");

            var sb2 = new StringBuilder();
            sb2.AppendLine($"[{path}] 共 {totalPages} 页，读取 {indices.Count} 页");

            foreach (var pageNum in indices)
            {
                var pageText = ExtractPageText(doc, pageNum);
                sb2.AppendLine();
                sb2.AppendLine($"[Page {pageNum}]");
                sb2.AppendLine(pageText);
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

    internal static string ExtractPageText(PdfDocument doc, int pageNum)
    {
        var page = doc.GetPage(pageNum);
        var words = page.GetWords();
        var sb = new StringBuilder();
        var lastY = double.MaxValue;

        foreach (var word in words)
        {
            // 检测换行：Y 坐标变化超过阈值
            var y = word.BoundingBox.Bottom;
            if (Math.Abs(y - lastY) > 5 && sb.Length > 0)
                sb.AppendLine();

            if (sb.Length > 0 && Math.Abs(y - lastY) < 5)
                sb.Append(' ');
            sb.Append(word.Text);
            lastY = y;
        }
        return sb.ToString().TrimEnd();
    }

    private static void AppendBookmarks(StringBuilder sb, IReadOnlyList<BookmarkNode> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            var indent = new string(' ', depth * 2);
            var pageRef = node is DocumentBookmarkNode dn ? $" (第{dn.PageNumber}页)" : "";
            sb.AppendLine($"{indent}- {node.Title}{pageRef}");
            if (node.Children.Count > 0)
                AppendBookmarks(sb, node.Children, depth + 1);
        }
    }
}
