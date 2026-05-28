// Plugins/Plugin.DocumentTools/WriteXlsxTool.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FileToolKit.Shared;

namespace Plugin.DocumentTools;

[ToolMeta(Group = "document", ContinueLoop = true, CapabilitySummary = "创建或追加 Excel 表格")]
public class WriteXlsxTool : FileToolBase
{
    public WriteXlsxTool(string workspaceDir) : base(workspaceDir) { }

    public override string Name => "write_xlsx";
    public override string Description => "创建或追加 Excel (.xlsx) 表格。data 参数为 CSV 格式（逗号分隔，换行分行）。action: write=覆盖创建（默认）/ append=追加到已有 sheet 末尾。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("path", "文件路径（相对于 Workspace 目录）", 0),
        new("data", "表格数据，CSV 格式（逗号分隔，\\n 分行）", 1),
        new("sheet", "（可选）sheet 名称，默认 \"Sheet1\"", 2, isRequired: false),
        new("action", "（可选）write=覆盖创建（默认）/ append=追加到已有 sheet", 3, isRequired: false)
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var data = resolvedInputs.Count > 1 ? resolvedInputs[1] : "";
        var sheetName = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";
        var action = resolvedInputs.Count > 3 ? resolvedInputs[3].Trim().ToLowerInvariant() : "write";

        if (string.IsNullOrEmpty(path))
            return Fail("path 不能为空");
        if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return Fail("文件扩展名必须是 .xlsx");
        if (string.IsNullOrEmpty(sheetName))
            sheetName = "Sheet1";

        var fullPath = ResolvePath(path);
        if (fullPath == null)
            return Fail("路径不合法：不能访问 Workspace 目录之外的文件");

        var isAppend = action == "append" && File.Exists(fullPath);

        try
        {
            var rows = ParseCsvData(data);
            if (rows.Count == 0)
                return Fail("data 参数为空");

            var dir = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(dir);

            int writtenRows;
            if (isAppend)
            {
                writtenRows = AppendToExisting(fullPath, sheetName, rows);
            }
            else
            {
                writtenRows = CreateNew(fullPath, sheetName, rows);
            }

            var size = new FileInfo(fullPath).Length;
            return Ok($"{(isAppend ? "已追加" : "已创建")} {path} ({FormatSize(size)})，写入 {writtenRows} 行");
        }
        catch (Exception ex)
        {
            return Fail($"写入失败: {ex.Message}");
        }
    }

    private static List<string[]> ParseCsvData(string data)
    {
        var rows = new List<string[]>();
        var lines = data.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            var cells = trimmed.Split(',');
            rows.Add(cells.Select(c => c.Trim()).ToArray());
        }
        return rows;
    }

    private static int CreateNew(string fullPath, string sheetName, List<string[]> rows)
    {
        using var doc = SpreadsheetDocument.Create(fullPath, SpreadsheetDocumentType.Workbook);
        var wbPart = doc.AddWorkbookPart();
        var wsPart = wbPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        WriteRows(sheetData, rows, 1);
        wsPart.Worksheet = new Worksheet(sheetData);

        var rId = wbPart.GetIdOfPart(wsPart);
        wbPart.Workbook = new Workbook(new Sheets(new Sheet
        {
            Id = rId,
            SheetId = 1U,
            Name = sheetName
        }));
        wbPart.Workbook.Save();
        return rows.Count;
    }

    private static int AppendToExisting(string fullPath, string sheetName, List<string[]> rows)
    {
        using var doc = SpreadsheetDocument.Open(fullPath, true);
        var wbPart = doc.WorkbookPart!;
        var sheet = wbPart.Workbook.Sheets?.Elements<Sheet>()
            .FirstOrDefault(s => string.Equals(s.Name?.Value, sheetName, StringComparison.OrdinalIgnoreCase));

        WorksheetPart wsPart;
        if (sheet?.Id?.Value is string rId && !string.IsNullOrEmpty(rId))
        {
            wsPart = (WorksheetPart)wbPart.GetPartById(rId);
        }
        else
        {
            wsPart = wbPart.AddNewPart<WorksheetPart>();
            wsPart.Worksheet = new Worksheet(new SheetData());
            var newSheetId = (wbPart.Workbook.Sheets?.Elements<Sheet>().Max(s => s.SheetId?.Value ?? 0) ?? 0) + 1U;
            wbPart.Workbook.Sheets!.Append(new Sheet
            {
                Id = wbPart.GetIdOfPart(wsPart),
                SheetId = newSheetId,
                Name = sheetName
            });
        }

        var sheetData = wsPart.Worksheet.Elements<SheetData>().First();
        var lastRow = sheetData.Elements<Row>().LastOrDefault()?.RowIndex?.Value ?? 0U;
        WriteRows(sheetData, rows, lastRow + 1);

        wsPart.Worksheet.Save();
        wbPart.Workbook.Save();
        return rows.Count;
    }

    private static void WriteRows(SheetData sheetData, List<string[]> rows, uint startRow)
    {
        var rowIndex = startRow;
        foreach (var rowData in rows)
        {
            var row = new Row { RowIndex = rowIndex };
            uint colIndex = 1;
            foreach (var cellValue in rowData)
            {
                var cell = new Cell
                {
                    CellReference = GetCellReference(colIndex, rowIndex),
                    DataType = CellValues.String,
                    CellValue = new CellValue(cellValue)
                };
                row.Append(cell);
                colIndex++;
            }
            sheetData.Append(row);
            rowIndex++;
        }
    }

    private static string GetCellReference(uint col, uint row)
    {
        var colStr = "";
        while (col > 0)
        {
            col--;
            colStr = (char)('A' + col % 26) + colStr;
            col /= 26;
        }
        return colStr + row;
    }
}
