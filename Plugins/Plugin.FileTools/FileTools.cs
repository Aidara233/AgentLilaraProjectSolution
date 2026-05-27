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
        public override string Description => "读取文本文件内容。路径相对于 Workspace 目录，只能访问该目录内的文件。支持指定行范围。";
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
                var lines = File.ReadAllLines(fullPath);
                int start = 1, end = lines.Length;

                if (int.TryParse(startStr, out var s) && s >= 1) start = s;
                if (int.TryParse(endStr, out var e) && e >= 1) end = Math.Min(e, lines.Length);
                if (start > lines.Length) return Ok($"(文件共 {lines.Length} 行，起始行超出范围)");

                var selected = lines[(start - 1)..end];
                var result = string.Join("\n", selected);

                if (result.Length > 8000)
                    result = result[..8000] + $"\n... (截断，文件共 {lines.Length} 行)";

                return Ok($"[{path}] 行 {start}-{end}/{lines.Length}\n{result}");
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
}
