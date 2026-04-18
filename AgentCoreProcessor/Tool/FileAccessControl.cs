using System;
using System.IO;
using System.Linq;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Tool
{
    internal static class FileAccessControl
    {
        private const long WorkspaceMaxBytes = 500L * 1024 * 1024;
        private const long WriteMaxBytes = 100L * 1024;
        private const long TransferMaxBytes = 10L * 1024 * 1024;

        private static string WorkspacePath => Path.Combine(PathConfig.StoragePath, "Workspace");

        private static readonly string[] BlacklistDirs = ["SSH"];
        private static readonly string[] BlacklistExtensions = [".key"];
        private static readonly string[] BlacklistFileNames = ["lilara.db"];

        public static string ResolvePath(string rawPath)
        {
            if (Path.IsPathRooted(rawPath))
                return Path.GetFullPath(rawPath);
            return Path.GetFullPath(Path.Combine(PathConfig.StoragePath, rawPath));
        }

        public static (bool Allowed, string? Error) CheckAccess(string fullPath)
        {
            var normalized = Path.GetFullPath(fullPath);
            var storageFull = Path.GetFullPath(PathConfig.StoragePath);

            if (!normalized.StartsWith(storageFull, StringComparison.OrdinalIgnoreCase))
                return (false, "禁止访问 Storage 目录之外的路径");

            var fileName = Path.GetFileName(normalized);
            if (BlacklistFileNames.Any(b => fileName.Equals(b, StringComparison.OrdinalIgnoreCase)))
                return (false, $"禁止访问受保护文件: {fileName}");

            var ext = Path.GetExtension(normalized);
            if (BlacklistExtensions.Any(b => ext.Equals(b, StringComparison.OrdinalIgnoreCase)))
                return (false, "禁止访问密钥文件");

            var relativePath = normalized[storageFull.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault() ?? "";
            if (BlacklistDirs.Any(b => firstSegment.Equals(b, StringComparison.OrdinalIgnoreCase)))
                return (false, "禁止访问 SSH 目录");

            return (true, null);
        }

        public static bool IsWorkspacePath(string fullPath)
        {
            var normalized = Path.GetFullPath(fullPath);
            var wsFull = Path.GetFullPath(WorkspacePath);
            return normalized.StartsWith(wsFull, StringComparison.OrdinalIgnoreCase);
        }

        public static (bool Ok, string? Error) CheckWriteSize(string content)
        {
            if (content.Length * 2 > WriteMaxBytes)
                return (false, $"写入内容超过大小限制（{WriteMaxBytes / 1024}KB）");
            return (true, null);
        }

        public static (bool Ok, string? Error) CheckTransferSize(long bytes)
        {
            if (bytes > TransferMaxBytes)
                return (false, $"文件超过传输大小限制（{TransferMaxBytes / 1024 / 1024}MB）");
            return (true, null);
        }

        public static (bool Ok, string? Error) CheckWorkspaceCapacity(long additionalBytes)
        {
            var wsPath = WorkspacePath;
            if (!Directory.Exists(wsPath))
                return (true, null);
            long total = Directory.EnumerateFiles(wsPath, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
            if (total + additionalBytes > WorkspaceMaxBytes)
                return (false, $"工作区空间不足（已用 {total / 1024 / 1024}MB / {WorkspaceMaxBytes / 1024 / 1024}MB）");
            return (true, null);
        }

        public static void EnsureWorkspaceExists()
        {
            var wsPath = WorkspacePath;
            if (!Directory.Exists(wsPath))
                Directory.CreateDirectory(wsPath);
        }
    }
}