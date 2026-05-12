using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace Plugin.FileTools
{
    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "读取文本文件内容")]
    public class ReadTextTool : ITool
    {
        private readonly string _workspaceDir;

        public string Name => "read_text";
        public string Description => "读取文本文件内容。路径相对于 Workspace 目录，只能访问该目录内的文件。支持指定行范围。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "文件路径（相对于 Workspace 目录）", 0),
            new("start_line", "（可选）起始行号，从1开始", 1, isRequired: false),
            new("end_line", "（可选）结束行号", 2, isRequired: false)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public ReadTextTool(IToolContext ctx)
        {
            _workspaceDir = Path.Combine(ctx.Storage.GlobalDirectory, "..", "..", "Workspace");
            _workspaceDir = Path.GetFullPath(_workspaceDir);
            Directory.CreateDirectory(_workspaceDir);
        }

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
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

        private string? ResolvePath(string relativePath)
        {
            var full = Path.GetFullPath(Path.Combine(_workspaceDir, relativePath));
            return full.StartsWith(_workspaceDir, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        private static Task<ToolResult> Ok(string data) =>
            Task.FromResult(new ToolResult { Status = "success", Data = data });
        private static Task<ToolResult> Fail(string err) =>
            Task.FromResult(new ToolResult { Status = "failed", Error = err });
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "写入或追加文本文件")]
    public class WriteTextTool : ITool
    {
        private readonly string _workspaceDir;

        public string Name => "write_text";
        public string Description => "写入文本文件。路径相对于 Workspace 目录。action: write(覆盖写入) / append(追加)。自动创建不存在的目录。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "文件路径（相对于 Workspace 目录）", 0),
            new("content", "要写入的文本内容", 1),
            new("action", "（可选）write=覆盖（默认）/ append=追加", 2, isRequired: false)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public WriteTextTool(IToolContext ctx)
        {
            _workspaceDir = Path.Combine(ctx.Storage.GlobalDirectory, "..", "..", "Workspace");
            _workspaceDir = Path.GetFullPath(_workspaceDir);
            Directory.CreateDirectory(_workspaceDir);
        }

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
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

        private string? ResolvePath(string relativePath)
        {
            var full = Path.GetFullPath(Path.Combine(_workspaceDir, relativePath));
            return full.StartsWith(_workspaceDir, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        private static Task<ToolResult> Ok(string data) =>
            Task.FromResult(new ToolResult { Status = "success", Data = data });
        private static Task<ToolResult> Fail(string err) =>
            Task.FromResult(new ToolResult { Status = "failed", Error = err });
    }
}
