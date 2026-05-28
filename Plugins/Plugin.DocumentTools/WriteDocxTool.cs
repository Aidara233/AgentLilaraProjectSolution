// Plugins/Plugin.DocumentTools/WriteDocxTool.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FileToolKit.Shared;

namespace Plugin.DocumentTools;

[ToolMeta(Group = "document", ContinueLoop = true, CapabilitySummary = "创建或覆盖 Word 文档")]
public class WriteDocxTool : FileToolBase
{
    public WriteDocxTool(string workspaceDir) : base(workspaceDir) { }

    public override string Name => "write_docx";
    public override string Description => "创建 Word (.docx) 文档。简单文本写入，不支持复杂格式。paragraphs 参数用换行分隔段落。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("path", "文件路径（相对于 Workspace 目录）", 0),
        new("title", "文档标题", 1),
        new("paragraphs", "段落内容，用 \\n 换行分隔", 2)
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var title = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
        var paragraphs = resolvedInputs.Count > 2 ? resolvedInputs[2] : "";

        if (string.IsNullOrEmpty(path))
            return Fail("path 不能为空");
        if (!path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            return Fail("文件扩展名必须是 .docx");

        var fullPath = ResolvePath(path);
        if (fullPath == null)
            return Fail("路径不合法：不能访问 Workspace 目录之外的文件");

        try
        {
            var dir = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(dir);

            using var doc = WordprocessingDocument.Create(fullPath, WordprocessingDocumentType.Document);
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            // 标题
            if (!string.IsNullOrEmpty(title))
            {
                body.AppendChild(new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    new Run(new Text(title))
                ));
            }

            // 段落
            var lines = paragraphs.Split('\n');
            var paraCount = 0;
            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd('\r');
                body.AppendChild(new Paragraph(new Run(new Text(trimmed))));
                paraCount++;
            }

            mainPart.Document.Save();

            var size = new FileInfo(fullPath).Length;
            return Ok($"已创建 {path} ({FormatSize(size)})，共 {paraCount} 段");
        }
        catch (Exception ex)
        {
            return Fail($"写入失败: {ex.Message}");
        }
    }
}
