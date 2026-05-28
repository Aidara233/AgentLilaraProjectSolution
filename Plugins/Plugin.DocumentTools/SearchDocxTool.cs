// Plugins/Plugin.DocumentTools/SearchDocxTool.cs
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

[ToolMeta(Group = "document", ContinueLoop = true, CapabilitySummary = "在 Word 文档中搜索关键词")]
public class SearchDocxTool : FileToolBase
{
    private const int ContextWindow = 50;
    private const int MaxResults = 50;

    public SearchDocxTool(string workspaceDir) : base(workspaceDir) { }

    public override string Name => "search_docx";
    public override string Description => "在 Word (.docx) 文档中搜索关键词，返回匹配位置和上下文。";
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
            using var doc = WordprocessingDocument.Open(stream, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null)
                return Ok($"[{path}] 文档为空，无匹配结果");

            var paragraphs = body.Elements<Paragraph>().ToList();
            var totalMatches = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"搜索 \"{keyword}\" in [{path}]:");

            for (var i = 0; i < paragraphs.Count; i++)
            {
                var p = paragraphs[i];
                var text = ReadDocxTool.ExtractParagraphText(p);
                var heading = ReadDocxTool.GetHeadingLevel(p);
                var paraNum = i + 1;

                var pos = 0;
                while (pos <= text.Length - keyword.Length)
                {
                    var idx = text.IndexOf(keyword, pos, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;

                    totalMatches++;
                    if (sb.Length < 10000 && totalMatches <= maxResults)
                    {
                        var ctxStart = Math.Max(0, idx - ContextWindow);
                        var ctxEnd = Math.Min(text.Length, idx + keyword.Length + ContextWindow);
                        var ctx = text[ctxStart..ctxEnd];
                        var prefix = ctxStart > 0 ? "..." : "";
                        var suffix = ctxEnd < text.Length ? "..." : "";
                        var headingTag = heading > 0 ? " [标题]" : "";

                        sb.AppendLine($"  P{paraNum}{headingTag}: {prefix}{ctx}{suffix}");
                    }

                    pos = idx + keyword.Length;
                }
            }

            sb.Insert(sb.ToString().IndexOf(':') + 1, $" 共 {totalMatches} 处匹配");
            if (totalMatches > maxResults)
                sb.AppendLine($"\n... (仅显示前 {maxResults} 条)");

            var result = sb.ToString().TrimEnd();
            if (result.Length > 10000)
                result = result[..10000] + $"\n... (结果已截断)";
            return Ok(result);
        }
        catch (Exception ex)
        {
            return Fail($"搜索失败: {ex.Message}");
        }
    }
}
