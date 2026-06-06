using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using FileToolKit.Shared;

namespace Plugin.FileTools
{
    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "读取文本文件内容")]
    public class ReadTextTool : FileToolBase
    {
        public ReadTextTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "read_text";
        public override string Description => "读取文本文件内容。路径相对于 Workspace 目录，只能访问该目录内的文件。支持指定行范围。建议先用 grep_files 定位到具体行号，再用本工具按行号范围读取，避免全量加载大文件。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "文件路径（相对于 Workspace 目录）", 0),
            new("start_line", "（可选）起始行号，从1开始", 1, isRequired: false),
            new("end_line", "（可选）结束行号", 2, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var startStr = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
            var endStr = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";

            if (string.IsNullOrEmpty(path))
                return Fail("path 不能为空");

            var fullPath = ResolvePath(path);
            if (fullPath == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外的文件");

            if (!File.Exists(fullPath))
                return Fail($"文件不存在: {path}");

            try
            {
                int start = 1, end = int.MaxValue;
                bool hasRange = false;
                if (int.TryParse(startStr, out var s) && s >= 1) { start = s; hasRange = true; }
                if (int.TryParse(endStr, out var e) && e >= 1) { end = e; hasRange = true; }

                var sb = new System.Text.StringBuilder();

                if (hasRange)
                {
                    using var reader = new StreamReader(fullPath);
                    string? line;
                    int lineNum = 0;
                    while ((line = reader.ReadLine()) != null && lineNum < end)
                    {
                        ct.ThrowIfCancellationRequested();
                        lineNum++;
                        if (lineNum < start) continue;
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(line);
                    }

                    if (start > lineNum)
                        return Ok($"(文件行数不足，起始行 {start} 超出当前行数 {lineNum})");

                    var result = sb.ToString();
                    if (result.Length > 8000)
                        result = result[..8000] + $"\n... (截断)";

                    return Ok($"[{path}] 行 {start}-{Math.Min(end, lineNum)}\n{result}");
                }
                else
                {
                    using var reader = new StreamReader(fullPath);
                    string? line;
                    int totalLines = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        ct.ThrowIfCancellationRequested();
                        totalLines++;
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(line);
                    }

                    var result = sb.ToString();
                    if (result.Length > 8000)
                        result = result[..8000] + $"\n... (截断，文件共 {totalLines} 行)";

                    return Ok($"[{path}] 共 {totalLines} 行\n{result}");
                }
            }
            catch (Exception ex)
            {
                return Fail($"读取失败: {ex.Message}");
            }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "写入或追加文本文件")]
    public class WriteTextTool : FileToolBase
    {
        public WriteTextTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "write_text";
        public override string Description => "写入文本文件。路径相对于 Workspace 目录。action: write(覆盖写入) / append(追加)。自动创建不存在的目录。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "文件路径（相对于 Workspace 目录）", 0),
            new("content", "要写入的文本内容", 1),
            new("action", "（可选）write=覆盖（默认）/ append=追加", 2, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var content = resolvedInputs.Count > 1 ? resolvedInputs[1] : "";
            var action = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim().ToLower() : "write";

            if (string.IsNullOrEmpty(path))
                return Fail("path 不能为空");

            var fullPath = ResolvePath(path);
            if (fullPath == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外的文件");

            try
            {
                var dir = Path.GetDirectoryName(fullPath)!;
                Directory.CreateDirectory(dir);

                if (action == "append")
                    File.AppendAllText(fullPath, content);
                else
                    File.WriteAllText(fullPath, content);

                var size = new FileInfo(fullPath).Length;
                return Ok($"已{(action == "append" ? "追加" : "写入")} {path} ({size} bytes)");
            }
            catch (Exception ex)
            {
                return Fail($"写入失败: {ex.Message}");
            }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "列出目录内容")]
    public class ListDirTool : FileToolBase
    {
        public ListDirTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "list_dir";
        public override string Description => "列出目录下的文件和子目录。路径相对于 Workspace 目录，为空则列出根目录。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "（可选）目录路径，相对于 Workspace，为空则列出根目录", 0, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var fullPath = string.IsNullOrEmpty(path)
                ? WorkspaceDir
                : ResolvePath(path);

            if (fullPath == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!Directory.Exists(fullPath))
                return Fail($"目录不存在: {path}");

            var sb = new System.Text.StringBuilder();
            var dirs = Directory.GetDirectories(fullPath);
            var files = Directory.GetFiles(fullPath);

            sb.AppendLine($"[{(string.IsNullOrEmpty(path) ? "/" : path)}] {dirs.Length} 个目录, {files.Length} 个文件");
            foreach (var d in dirs)
                sb.AppendLine($"  📁 {Path.GetFileName(d)}/");
            foreach (var f in files)
            {
                var info = new FileInfo(f);
                sb.AppendLine($"  📄 {info.Name} ({FormatSize(info.Length)})");
            }
            return Ok(sb.ToString().TrimEnd());
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true)]
    public class MoveFileTool : FileToolBase
    {
        public MoveFileTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "move_file";
        public override string Description => "移动或重命名文件/目录。源和目标路径都相对于 Workspace 目录。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("source", "源路径", 0),
            new("destination", "目标路径", 1)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var src = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var dst = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
                return Fail("source 和 destination 都不能为空");

            var srcFull = ResolvePath(src);
            var dstFull = ResolvePath(dst);
            if (srcFull == null || dstFull == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外");

            try
            {
                var dstDir = Path.GetDirectoryName(dstFull)!;
                Directory.CreateDirectory(dstDir);

                if (Directory.Exists(srcFull))
                    Directory.Move(srcFull, dstFull);
                else if (File.Exists(srcFull))
                    File.Move(srcFull, dstFull, overwrite: true);
                else
                    return Fail($"源不存在: {src}");

                return Ok($"已移动: {src} → {dst}");
            }
            catch (Exception ex) { return Fail($"移动失败: {ex.Message}"); }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true)]
    public class DeleteFileTool : FileToolBase
    {
        public DeleteFileTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "delete_file";
        public override string Description => "删除文件或空目录。路径相对于 Workspace 目录。非空目录需要先清空。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "要删除的文件或空目录路径", 0)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            if (string.IsNullOrEmpty(path))
                return Fail("path 不能为空");

            var fullPath = ResolvePath(path);
            if (fullPath == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外");

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    return Ok($"已删除文件: {path}");
                }
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive: false);
                    return Ok($"已删除目录: {path}");
                }
                return Fail($"不存在: {path}");
            }
            catch (IOException ex) when (ex.Message.Contains("not empty"))
            {
                return Fail($"目录非空，无法删除: {path}");
            }
            catch (Exception ex) { return Fail($"删除失败: {ex.Message}"); }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true)]
    public class CopyFileTool : FileToolBase
    {
        public CopyFileTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "copy_file";
        public override string Description => "复制文件。源和目标路径都相对于 Workspace 目录。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("source", "源文件路径", 0),
            new("destination", "目标文件路径", 1)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var src = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var dst = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
                return Fail("source 和 destination 都不能为空");

            var srcFull = ResolvePath(src);
            var dstFull = ResolvePath(dst);
            if (srcFull == null || dstFull == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外");

            if (!File.Exists(srcFull))
                return Fail($"源文件不存在: {src}");

            try
            {
                var dstDir = Path.GetDirectoryName(dstFull)!;
                Directory.CreateDirectory(dstDir);
                File.Copy(srcFull, dstFull, overwrite: true);
                return Ok($"已复制: {src} → {dst}");
            }
            catch (Exception ex) { return Fail($"复制失败: {ex.Message}"); }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "局部修改文本文件")]
    public class UpdateTextTool : FileToolBase
    {
        public UpdateTextTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "update_text";
        public override string Description =>
            "局部修改文本文件内容，无需重写整个文件。路径相对于 Workspace 目录。\n\n" +
            "支持四种模式（通过 mode 参数选择）：\n\n" +
            "  replace_line  — 替换单行。需要 line（行号，从1开始）和 content（新内容）。\n" +
            "                  例：mode=replace_line, line=5, content=\"新文本\"\n\n" +
            "  replace_range — 替换连续行范围。需要 start_line、end_line 和 content。\n" +
            "                  例：mode=replace_range, start_line=10, end_line=20, content=\"替换后的内容\"\n\n" +
            "  search_replace — 按文本查找并替换。需要 search（要查找的文本）和 replace（替换文本）。\n" +
            "                  可选 replace_count 控制替换次数：first=仅第一个（默认），all=全部。\n" +
            "                  search 文本必须与文件内容完全匹配（包含缩进和换行），多行文本用 \\n 连接。\n" +
            "                  例：mode=search_replace, search=\"旧标题\", replace=\"新标题\", replace_count=all\n\n" +
            "  insert_at     — 在指定行前或行后插入新内容。需要 line（行号）、position（before/after）和 content。\n" +
            "                  例：mode=insert_at, line=8, position=after, content=\"插入的新行\"\n\n" +
            "所有模式修改成功后会返回修改后的文件总行数和变更摘要。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "文件路径（相对于 Workspace 目录）", 0),
            new("mode", "修改模式：replace_line / replace_range / search_replace / insert_at", 1),
            new("line", "行号（replace_line 和 insert_at 模式使用，从1开始）", 2, isRequired: false),
            new("start_line", "起始行号（replace_range 模式使用，从1开始）", 3, isRequired: false),
            new("end_line", "结束行号（replace_range 模式使用）", 4, isRequired: false),
            new("content", "新内容（replace_line / replace_range / insert_at 模式使用）", 5, isRequired: false),
            new("search", "要查找的文本（search_replace 模式使用）", 6, isRequired: false),
            new("replace", "替换文本（search_replace 模式使用）", 7, isRequired: false),
            new("replace_count", "替换次数控制：first（默认）/ all（search_replace 模式使用）", 8, isRequired: false),
            new("position", "插入位置：before（行前）/ after（行后），insert_at 模式使用", 9, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = Get(resolvedInputs, 0).Trim();
            var mode = Get(resolvedInputs, 1).Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(path))
                return Fail("path 不能为空");
            if (string.IsNullOrEmpty(mode))
                return Fail("mode 不能为空，可选值：replace_line / replace_range / search_replace / insert_at");

            var fullPath = ResolvePath(path);
            if (fullPath == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外的文件");
            if (!File.Exists(fullPath))
                return Fail($"文件不存在: {path}");

            try
            {
                var lines = File.ReadAllLines(fullPath);

                var result = mode switch
                {
                    "replace_line" => DoReplaceLine(lines, resolvedInputs, path),
                    "replace_range" => DoReplaceRange(lines, resolvedInputs, path),
                    "search_replace" => DoSearchReplace(lines, resolvedInputs, fullPath, path),
                    "insert_at" => DoInsertAt(lines, resolvedInputs, path),
                    _ => Fail($"未知模式: {mode}。可选：replace_line / replace_range / search_replace / insert_at")
                };

                return result;
            }
            catch (Exception ex)
            {
                return Fail($"修改失败: {ex.Message}");
            }
        }

        private Task<ToolResult> DoReplaceLine(string[] lines, List<string> inputs, string path)
        {
            var lineStr = Get(inputs, 2).Trim();
            var content = Get(inputs, 5);

            if (!int.TryParse(lineStr, out var line) || line < 1)
                return Fail("line 参数无效，需要是从1开始的行号");
            if (line > lines.Length)
                return Fail($"line={line} 超出文件范围（文件共 {lines.Length} 行）");

            lines[line - 1] = content;
            WriteBack(path, lines);
            return Ok($"已替换第 {line} 行（文件共 {lines.Length} 行）");
        }

        private Task<ToolResult> DoReplaceRange(string[] lines, List<string> inputs, string path)
        {
            var startStr = Get(inputs, 3).Trim();
            var endStr = Get(inputs, 4).Trim();
            var content = Get(inputs, 5);

            if (!int.TryParse(startStr, out var start) || start < 1)
                return Fail("start_line 参数无效");
            if (!int.TryParse(endStr, out var end) || end < start)
                return Fail("end_line 参数无效或小于 start_line");
            if (end > lines.Length)
                return Fail($"end_line={end} 超出文件范围（文件共 {lines.Length} 行）");

            var newContentLines = content.Split('\n');
            var before = lines[..(start - 1)];
            var after = lines[end..];
            var merged = new string[before.Length + newContentLines.Length + after.Length];
            before.CopyTo(merged, 0);
            newContentLines.CopyTo(merged, before.Length);
            after.CopyTo(merged, before.Length + newContentLines.Length);

            WriteBack(path, merged);
            return Ok($"已替换第 {start}-{end} 行（{end - start + 1} 行 → {newContentLines.Length} 行，文件现共 {merged.Length} 行）");
        }

        private Task<ToolResult> DoSearchReplace(string[] lines, List<string> inputs, string fullPath, string path)
        {
            var search = Get(inputs, 6);
            var replace = Get(inputs, 7);
            var countMode = Get(inputs, 8).Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(search))
                return Fail("search 参数不能为空");

            var replaceAll = countMode == "all";
            var fullText = string.Join("\n", lines);

            if (!fullText.Contains(search))
                return Fail($"未找到匹配的搜索文本。请确保 search 内容与文件中的实际文本完全匹配（包括缩进和换行）。");

            int count = 0;
            string newText;
            if (replaceAll)
            {
                newText = fullText.Replace(search, replace);
                // 简单计数：统计原字符串中 search 出现的次数
                var idx = 0;
                while ((idx = fullText.IndexOf(search, idx, StringComparison.Ordinal)) >= 0)
                {
                    count++;
                    idx += search.Length;
                }
            }
            else
            {
                var pos = fullText.IndexOf(search, StringComparison.Ordinal);
                newText = fullText[..pos] + replace + fullText[(pos + search.Length)..];
                count = 1;
            }

            var newLines = newText.Split('\n');
            WriteBack(path, newLines);
            return Ok($"已替换 {count} 处匹配（文件现共 {newLines.Length} 行）");
        }

        private Task<ToolResult> DoInsertAt(string[] lines, List<string> inputs, string path)
        {
            var lineStr = Get(inputs, 2).Trim();
            var position = Get(inputs, 9).Trim().ToLowerInvariant();
            var content = Get(inputs, 5);

            if (!int.TryParse(lineStr, out var line) || line < 1)
                return Fail("line 参数无效，需要是从1开始的行号");
            if (line > lines.Length + 1)
                return Fail($"line={line} 超出文件范围（文件共 {lines.Length} 行）");
            if (position != "before" && position != "after")
                return Fail("position 参数必须是 before 或 after");

            var insertLines = content.Split('\n');
            var insertPos = position == "before" ? line - 1 : line;
            var merged = new string[lines.Length + insertLines.Length];
            lines[..insertPos].CopyTo(merged, 0);
            insertLines.CopyTo(merged, insertPos);
            lines[insertPos..].CopyTo(merged, insertPos + insertLines.Length);

            WriteBack(path, merged);
            return Ok($"已在第 {line} 行{(position == "before" ? "前" : "后")}插入 {insertLines.Length} 行（文件现共 {merged.Length} 行）");
        }

        private void WriteBack(string path, string[] lines)
        {
            var fullPath = ResolvePath(path)!;
            File.WriteAllLines(fullPath, lines);
        }

        private static string Get(List<string> list, int index) =>
            index < list.Count ? list[index] : "";
    }
}
