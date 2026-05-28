// Plugins/Plugin.DocumentTools/SearchXlsxTool.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FileToolKit.Shared;

namespace Plugin.DocumentTools;

[ToolMeta(Group = "document", ContinueLoop = true, CapabilitySummary = "在 Excel 表格中搜索关键词")]
public class SearchXlsxTool : FileToolBase
{
    private const int MaxResults = 50;
    private const int CellTruncateLen = 100;

    public SearchXlsxTool(string workspaceDir) : base(workspaceDir) { }

    public override string Name => "search_xlsx";
    public override string Description => "在 Excel (.xlsx) 表格中搜索关键词，返回匹配单元格的位置和内容。";
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
            using var doc = SpreadsheetDocument.Open(stream, false);
            var wbPart = doc.WorkbookPart;
            if (wbPart == null)
                return Ok($"[{path}] 表格为空，无匹配结果");

            var sheets = wbPart.Workbook.Sheets?.Elements<Sheet>().ToList() ?? new List<Sheet>();
            var totalMatches = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"搜索 \"{keyword}\" in [{path}]:");

            foreach (var sheet in sheets)
            {
                var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!);
                var sheetData = wsPart.Worksheet.Elements<SheetData>().FirstOrDefault();
                if (sheetData == null) continue;

                var sst = wbPart.SharedStringTablePart?.SharedStringTable;
                var rowNum = 0;
                foreach (var row in sheetData.Elements<Row>())
                {
                    rowNum++;
                    foreach (var cell in row.Elements<Cell>())
                    {
                        var value = ReadXlsxTool.GetCellValue(cell, sst);
                        if (value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        totalMatches++;
                        if (totalMatches <= maxResults && sb.Length < 10000)
                        {
                            var display = value.Length > CellTruncateLen
                                ? value[..CellTruncateLen] + "..."
                                : value;
                            sb.AppendLine($"  {sheet.Name}!{cell.CellReference?.Value} (行{rowNum}): {display}");
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
}
