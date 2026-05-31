using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace FileToolKit.Shared
{
    public abstract class FileToolBase : ITool
    {
        protected readonly string WorkspaceDir;

        protected FileToolBase(string workspaceDir)
        {
            WorkspaceDir = Path.GetFullPath(workspaceDir);
            Directory.CreateDirectory(WorkspaceDir);
        }

        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract IReadOnlyList<ToolParameter> Parameters { get; }
        public abstract TimeSpan Timeout { get; }
        public abstract Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct);

        protected string? ResolvePath(string relativePath)
        {
            var sanitized = relativePath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(WorkspaceDir, sanitized));
            var workspaceRoot = WorkspaceDir.EndsWith(Path.DirectorySeparatorChar)
                ? WorkspaceDir : WorkspaceDir + Path.DirectorySeparatorChar;
            return full.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)
                || full.Equals(WorkspaceDir, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        protected static Task<ToolResult> Ok(string data) =>
            Task.FromResult(new ToolResult { Status = "success", Data = data });

        protected static Task<ToolResult> Fail(string error) =>
            Task.FromResult(new ToolResult { Status = "failed", Error = error });

        protected static string TruncateWithSummary(string text, int maxLen, int totalCount, string itemLabel)
        {
            if (text.Length <= maxLen) return text;
            return text[..maxLen] + $"\n... (结果已截断，共 {totalCount} {itemLabel})";
        }

        /// <summary>从扩展名推断归档格式</summary>
        protected static string DetectFormat(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".zip" => "zip",
                ".tar" => "tar",
                ".gz" or ".tgz" => "tar.gz",
                ".7z" => "7z",
                _ => ext.TrimStart('.')
            };
        }

        protected static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes}B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
            _ => $"{bytes / (1024.0 * 1024):F1}MB"
        };

        /// <summary>简单 glob 转正则，支持 ** * ?。调用方应缓存结果，Compiled 有开销。</summary>
        protected static Regex ConvertGlobToRegex(string glob)
        {
            // 折叠 3+ 个连续 * 为 **
            glob = Regex.Replace(glob, @"\*{3,}", "**");
            var pattern = Regex.Escape(glob)
                .Replace("\\*\\*", "~~~DOTSTAR~~~")
                .Replace("\\*", "[^/\\\\]*")
                .Replace("\\?", "[^/\\\\]")
                .Replace("~~~DOTSTAR~~~", ".*");
            return new Regex($"^{pattern}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }
}
