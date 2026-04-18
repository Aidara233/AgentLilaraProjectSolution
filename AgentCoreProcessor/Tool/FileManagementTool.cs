using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Tool
{
    internal class FileManagementTool : ITool
    {
        public string Name => "文件管理";
        public string Description => "管理 Storage 目录内的文件和文件夹。支持 list（列目录）、mkdir（创建文件夹）、delete（删除，仅限 Workspace）、move（移动/重命名）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("操作", "list / mkdir / delete / move", 0),
            new("路径", "目标路径（相对 Storage/），list 和 mkdir 填目录路径，delete 填要删除的路径", 1),
            new("目标路径", "move 操作的目标路径（相对 Storage/），其他操作不填", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public bool AllowSubAgent => false;
        public PermissionLevel RequiredPermission => PermissionLevel.Default;
        public bool ContinueLoop => true;
        public bool RetainResult => false;
        public string? CapabilitySummary => "管理文件和文件夹";

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var op = (resolvedInputs.ElementAtOrDefault(0) ?? "").Trim().ToLower();
            var rawPath = resolvedInputs.ElementAtOrDefault(1) ?? "";
            var rawTarget = resolvedInputs.ElementAtOrDefault(2) ?? "";

            return Task.FromResult(op switch
            {
                "list" or "ls" => ListDirectory(rawPath),
                "mkdir" => MakeDirectory(rawPath),
                "delete" or "rm" or "删除" => DeletePath(rawPath),
                "move" or "mv" or "移动" => MovePath(rawPath, rawTarget),
                _ => new ToolResult { Status = "failed", Error = "操作必须是 list / mkdir / delete / move" }
            });
        }

        private static ToolResult ListDirectory(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                rawPath = ".";

            var fullPath = FileAccessControl.ResolvePath(rawPath);
            var (allowed, error) = FileAccessControl.CheckAccess(fullPath);
            if (!allowed)
                return new ToolResult { Status = "failed", Error = error };

            if (!Directory.Exists(fullPath))
                return new ToolResult { Status = "failed", Error = $"目录不存在: {rawPath}" };

            var sb = new StringBuilder();
            try
            {
                var dirs = Directory.GetDirectories(fullPath)
                    .Select(d => new DirectoryInfo(d))
                    .OrderBy(d => d.Name);
                foreach (var d in dirs)
                    sb.AppendLine($"  [目录] {d.Name}/");

                var files = Directory.GetFiles(fullPath)
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.Name);
                foreach (var f in files)
                    sb.AppendLine($"  {f.Name}  ({FormatSize(f.Length)})");

                if (sb.Length == 0)
                    sb.AppendLine("  （空目录）");
            }
            catch (Exception ex)
            {
                return new ToolResult { Status = "failed", Error = $"列目录失败: {ex.Message}" };
            }

            return new ToolResult { Status = "success", Data = $"{rawPath}/\n{sb.ToString().TrimEnd()}" };
        }

        private static ToolResult MakeDirectory(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return new ToolResult { Status = "failed", Error = "路径不能为空" };

            var fullPath = FileAccessControl.ResolvePath(rawPath);
            var (allowed, error) = FileAccessControl.CheckAccess(fullPath);
            if (!allowed)
                return new ToolResult { Status = "failed", Error = error };

            if (Directory.Exists(fullPath))
                return new ToolResult { Status = "success", Data = $"目录已存在: {rawPath}" };

            try
            {
                Directory.CreateDirectory(fullPath);
            }
            catch (Exception ex)
            {
                return new ToolResult { Status = "failed", Error = $"创建失败: {ex.Message}" };
            }

            return new ToolResult { Status = "success", Data = $"已创建: {rawPath}/" };
        }

        private static ToolResult DeletePath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return new ToolResult { Status = "failed", Error = "路径不能为空" };

            var fullPath = FileAccessControl.ResolvePath(rawPath);
            var (allowed, error) = FileAccessControl.CheckAccess(fullPath);
            if (!allowed)
                return new ToolResult { Status = "failed", Error = error };

            if (!FileAccessControl.IsWorkspacePath(fullPath))
                return new ToolResult { Status = "failed", Error = "删除操作仅限 Workspace/ 目录内" };

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    return new ToolResult { Status = "success", Data = $"已删除文件: {rawPath}" };
                }
                if (Directory.Exists(fullPath))
                {
                    if (Directory.EnumerateFileSystemEntries(fullPath).Any())
                        return new ToolResult { Status = "failed", Error = "目录非空，不能删除（防止误删）" };
                    Directory.Delete(fullPath);
                    return new ToolResult { Status = "success", Data = $"已删除空目录: {rawPath}" };
                }
                return new ToolResult { Status = "failed", Error = $"路径不存在: {rawPath}" };
            }
            catch (Exception ex)
            {
                return new ToolResult { Status = "failed", Error = $"删除失败: {ex.Message}" };
            }
        }

        private static ToolResult MovePath(string rawSource, string rawTarget)
        {
            if (string.IsNullOrWhiteSpace(rawSource))
                return new ToolResult { Status = "failed", Error = "源路径不能为空" };
            if (string.IsNullOrWhiteSpace(rawTarget))
                return new ToolResult { Status = "failed", Error = "目标路径不能为空" };

            var srcFull = FileAccessControl.ResolvePath(rawSource);
            var dstFull = FileAccessControl.ResolvePath(rawTarget);

            var (srcOk, srcErr) = FileAccessControl.CheckAccess(srcFull);
            if (!srcOk) return new ToolResult { Status = "failed", Error = srcErr };
            var (dstOk, dstErr) = FileAccessControl.CheckAccess(dstFull);
            if (!dstOk) return new ToolResult { Status = "failed", Error = dstErr };

            try
            {
                if (File.Exists(srcFull))
                {
                    var dir = Path.GetDirectoryName(dstFull);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.Move(srcFull, dstFull, overwrite: false);
                    return new ToolResult { Status = "success", Data = $"已移动: {rawSource} → {rawTarget}" };
                }
                if (Directory.Exists(srcFull))
                {
                    Directory.Move(srcFull, dstFull);
                    return new ToolResult { Status = "success", Data = $"已移动目录: {rawSource} → {rawTarget}" };
                }
                return new ToolResult { Status = "failed", Error = $"源路径不存在: {rawSource}" };
            }
            catch (Exception ex)
            {
                return new ToolResult { Status = "failed", Error = $"移动失败: {ex.Message}" };
            }
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes}B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
            _ => $"{bytes / 1024.0 / 1024.0:F1}MB"
        };
    }
}
