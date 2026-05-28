// Plugins/Plugin.DocumentTools/ReadXlsxTool.cs
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

[ToolMeta(Group = "document", ContinueLoop = true, CapabilitySummary = "读取 Excel 表格内容")]
public class ReadXlsxTool : FileToolBase
{
    private const int MaxCells = 5000;
    private const int PreviewRows = 20;

    public ReadXlsxTool(string workspaceDir) : base(workspaceDir) { }

    public override string Name => "read_xlsx";
    public override string Description => "读取 Excel (.xlsx) 表格。不提供全表提取——先不带 range 参数调用获取 sheet 列表和预览，再按需指定 range 读取区域。range 格式如 \"A1:D50\" 或 \"Sheet2!A1:C20\"。单次最多 5000 个单元格。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("path", "文件路径（相对于 Workspace 目录）", 0),
        new("range", "（可选）单元格区域，如 \"A1:D50\" 或 \"Sheet2!A1:C20\"。不带 sheet 名则读取第一个 sheet", 1, isRequired: false)
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
            using var doc = SpreadsheetDocument.Open(stream, false);
            var wbPart = doc.WorkbookPart;
            if (wbPart == null)
                return Fail("文档为空或格式损坏");

            var sheets = wbPart.Workbook.Sheets?.Elements<Sheet>().ToList() ?? new List<Sheet>();
            if (sheets.Count == 0)
                return Ok($"[{path}] 表格为空，无 sheet");

            // 构建 sheet 信息列表
            var sheetInfos = new List<(string Name, int Rows, int Cols)>();
            foreach (var sheet in sheets)
            {
                var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!);
                var sheetData = wsPart.Worksheet.Elements<SheetData>().FirstOrDefault();
                var rows = sheetData?.Elements<Row>().Count() ?? 0;
                var cols = 0;
                if (sheetData != null)
                {
                    foreach (var row in sheetData.Elements<Row>())
                    {
                        var colCount = row.Elements<Cell>().Count();
                        if (colCount > cols) cols = colCount;
                    }
                }
                sheetInfos.Add((sheet.Name!, rows, cols));
            }

            if (string.IsNullOrEmpty(range))
            {
                // 元数据 + 预览模式
                var sb = new StringBuilder();
                sb.AppendLine($"[{path}] 共 {sheets.Count} 个 sheet:");
                for (var i = 0; i < sheetInfos.Count; i++)
                {
                    var info = sheetInfos[i];
                    sb.AppendLine($"  [{i + 1}] {info.Name} — {info.Rows} 行 × {info.Cols} 列");
                }
                sb.AppendLine();
                sb.AppendLine($"--- 预览（{sheetInfos[0].Name} 前 {PreviewRows} 行）---");

                var previewCsv = ExtractSheetAsCsv(wbPart, sheets[0], PreviewRows, int.MaxValue, out _);
                sb.Append(previewCsv);

                var result = sb.ToString().TrimEnd();
                if (result.Length > 10000)
                    result = result[..10000] + $"\n... (结果已截断)";
                return Ok(result);
            }

            // 范围读取模式
            var (sheetName, cellRange) = ParseRangeExpression(range, sheets[0].Name!);
            var targetSheet = sheets.FirstOrDefault(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase))
                ?? sheets[0];

            var csv = ExtractRangeAsCsv(wbPart, targetSheet, cellRange, MaxCells, out var cellCount, out var truncated);
            var header = $"[{path}] {targetSheet.Name}!{cellRange} — {cellCount} 个单元格{(truncated ? " (已截断)" : "")}";
            var result2 = $"{header}\n{csv.TrimEnd()}";
            if (result2.Length > 10000)
                result2 = result2[..10000] + "\n... (结果已截断)";
            return Ok(result2);
        }
        catch (Exception ex)
        {
            return Fail($"读取失败: {ex.Message}");
        }
    }

    private static (string SheetName, string CellRange) ParseRangeExpression(string range, string defaultSheet)
    {
        var bangIdx = range.IndexOf('!');
        if (bangIdx > 0)
        {
            var sheetName = range[..bangIdx].Trim('\'', '"');
            var cellRange = range[(bangIdx + 1)..].Trim();
            return (sheetName, cellRange);
        }
        return (defaultSheet, range.Trim());
    }

    private static string ExtractRangeAsCsv(WorkbookPart wbPart, Sheet sheet, string range, int maxCells,
        out int cellCount, out bool truncated)
    {
        var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!);
        var sheetData = wsPart.Worksheet.Elements<SheetData>().FirstOrDefault();
        var sb = new StringBuilder();
        cellCount = 0;
        truncated = false;

        if (sheetData == null) return "";

        var (startRow, startCol, endRow, endCol) = ParseCellRange(range);
        var sst = wbPart.SharedStringTablePart?.SharedStringTable;

        var rows = sheetData.Elements<Row>().ToList();
        for (var r = startRow; r <= endRow && r < rows.Count; r++)
        {
            var row = rows[r];
            var cells = row.Elements<Cell>().ToList();
            for (var c = startCol; c <= endCol; c++)
            {
                if (cellCount >= maxCells)
                {
                    truncated = true;
                    goto done;
                }
                var cell = cells.FirstOrDefault(x => GetColumnIndex(x.CellReference!) == c);
                var value = cell != null ? GetCellValue(cell, sst) : "";
                if (c > startCol) sb.Append(',');
                sb.Append(EscapeCsv(value));
                cellCount++;
            }
            sb.AppendLine();
        }

        done:
        return sb.ToString();
    }

    internal static string ExtractSheetAsCsv(WorkbookPart wbPart, Sheet sheet, int maxRows, int maxCells,
        out bool truncated)
    {
        var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!);
        var sheetData = wsPart.Worksheet.Elements<SheetData>().FirstOrDefault();
        var sb = new StringBuilder();
        var cellCount = 0;
        truncated = false;

        if (sheetData == null) return "";

        var sst = wbPart.SharedStringTablePart?.SharedStringTable;
        var rows = sheetData.Elements<Row>().Take(maxRows).ToList();

        foreach (var row in rows)
        {
            var cells = row.Elements<Cell>().ToList();
            for (var i = 0; i < cells.Count; i++)
            {
                if (cellCount >= maxCells)
                {
                    truncated = true;
                    goto done;
                }
                if (i > 0) sb.Append(',');
                sb.Append(EscapeCsv(GetCellValue(cells[i], sst)));
                cellCount++;
            }
            sb.AppendLine();
        }

        done:
        return sb.ToString();
    }

    private static (int StartRow, int StartCol, int EndRow, int EndCol) ParseCellRange(string range)
    {
        var parts = range.Split(':');
        if (parts.Length == 2)
        {
            var (r1, c1) = ParseCellRef(parts[0].Trim());
            var (r2, c2) = ParseCellRef(parts[1].Trim());
            return (Math.Min(r1, r2) - 1, Math.Min(c1, c2) - 1, Math.Max(r1, r2) - 1, Math.Max(c1, c2) - 1);
        }
        // 单个单元格或整行/整列
        var (r, c) = ParseCellRef(range.Trim());
        return (r - 1, c - 1, r - 1, c - 1);
    }

    private static (int Row, int Col) ParseCellRef(string cellRef)
    {
        var colStr = "";
        var rowStr = "";
        foreach (var ch in cellRef)
        {
            if (char.IsLetter(ch)) colStr += ch;
            else if (char.IsDigit(ch)) rowStr += ch;
        }
        var col = 0;
        foreach (var ch in colStr.ToUpperInvariant())
            col = col * 26 + (ch - 'A' + 1);
        var row = int.TryParse(rowStr, out var r) ? r : 1;
        return (row, col);
    }

    private static int GetColumnIndex(string cellRef)
    {
        var col = 0;
        foreach (var ch in cellRef)
        {
            if (!char.IsLetter(ch)) break;
            col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return col;
    }

    internal static string GetCellValue(Cell cell, SharedStringTable? sst)
    {
        var value = cell.CellValue?.Text ?? "";
        if (cell.DataType?.Value == CellValues.SharedString && sst != null && int.TryParse(value, out var idx))
        {
            var item = sst.ElementAt(idx);
            return item.InnerText;
        }
        return value;
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
