// Plugins/Plugin.FileOps/MetaTools.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using FileToolKit.Shared;

namespace Plugin.FileOps
{
    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "获取文件详细信息")]
    public class FileInfoTool : FileToolBase
    {
        public FileInfoTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "file_info";
        public override string Description => "获取文件/目录的详细信息：大小、创建/修改时间、MIME类型、文本文件行数。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "文件或目录路径", 0)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            if (string.IsNullOrEmpty(path)) return Fail("path 不能为空");

            var fullPath = ResolvePath(path);
            if (fullPath == null) return Fail("路径不合法：不能访问 Workspace 目录之外");

            try
            {
                if (File.Exists(fullPath))
                {
                    var info = new FileInfo(fullPath);
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("类型: 文件");
                    sb.AppendLine($"大小: {FormatSize(info.Length)} ({info.Length} bytes)");
                    sb.AppendLine($"创建时间: {info.CreationTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"修改时间: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"MIME: {GetMimeType(info.Extension)}");

                    if (IsTextFile(info.Extension))
                    {
                        try
                        {
                            var lineCount = File.ReadLines(fullPath).Count();
                            sb.AppendLine($"行数: {lineCount}");
                        }
                        catch { sb.AppendLine("行数: (读取失败)"); }
                    }

                    return Ok(sb.ToString().TrimEnd());
                }
                else if (Directory.Exists(fullPath))
                {
                    var info = new DirectoryInfo(fullPath);
                    var files = Directory.GetFiles(fullPath).Length;
                    var dirs = Directory.GetDirectories(fullPath).Length;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("类型: 目录");
                    sb.AppendLine($"创建时间: {info.CreationTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"修改时间: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"包含: {files} 个文件, {dirs} 个子目录");

                    try
                    {
                        var totalSize = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories)
                            .Sum(f => new FileInfo(f).Length);
                        sb.AppendLine($"总大小: {FormatSize(totalSize)}");
                    }
                    catch { /* 权限不足时忽略 */ }

                    return Ok(sb.ToString().TrimEnd());
                }
                return Fail($"不存在: {path}");
            }
            catch (Exception ex) { return Fail($"获取信息失败: {ex.Message}"); }
        }

        private static string GetMimeType(string extension) => extension.ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".ts" => "text/typescript",
            ".cs" => "text/x-csharp",
            ".csproj" => "text/xml",
            ".sln" => "text/plain",
            ".py" => "text/x-python",
            ".yaml" or ".yml" => "text/yaml",
            ".toml" => "application/toml",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".tar" => "application/x-tar",
            ".gz" => "application/gzip",
            ".7z" => "application/x-7z-compressed",
            ".dll" => "application/x-msdownload",
            ".exe" => "application/x-msdownload",
            _ => "application/octet-stream"
        };

        private static bool IsTextFile(string extension) => extension.ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".json" or ".xml" or ".html" or ".htm" or ".css"
                or ".js" or ".ts" or ".cs" or ".py" or ".yaml" or ".yml" or ".toml"
                or ".csproj" or ".sln" or ".svg" or ".csv" or ".log"
                or ".config" or ".props" or ".targets" => true,
            _ => false
        };
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "计算文件哈希值")]
    public class FileHashTool : FileToolBase
    {
        public FileHashTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "file_hash";
        public override string Description => "计算文件的哈希值。支持 MD5 和 SHA256，默认 SHA256。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "文件路径", 0),
            new("algorithm", "（可选）md5 / sha256，默认 sha256", 1, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var algo = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToLower() : "sha256";

            if (string.IsNullOrEmpty(path)) return Fail("path 不能为空");

            var fullPath = ResolvePath(path);
            if (fullPath == null) return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!File.Exists(fullPath)) return Fail($"文件不存在: {path}");

            try
            {
                using var stream = File.OpenRead(fullPath);
                byte[]? hashBytes = algo switch
                {
                    "md5" => MD5.HashData(stream),
                    "sha256" => SHA256.HashData(stream),
                    _ => null
                };

                if (hashBytes == null)
                    return Fail($"不支持的算法: {algo}。支持 md5 / sha256");

                var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                return Ok($"{algo.ToUpper()}: {hash}");
            }
            catch (Exception ex) { return Fail($"计算哈希失败: {ex.Message}"); }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "对比两个文本文件")]
    public class CompareFilesTool : FileToolBase
    {
        public CompareFilesTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "compare_files";
        public override string Description => "逐行对比两个文本文件，返回差异摘要。最多显示前 100 行差异。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("source", "源文件路径", 0),
            new("target", "目标文件路径", 1)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var src = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var dst = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
                return Fail("source 和 target 都不能为空");

            var srcFull = ResolvePath(src);
            var dstFull = ResolvePath(dst);
            if (srcFull == null || dstFull == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!File.Exists(srcFull)) return Fail($"源文件不存在: {src}");
            if (!File.Exists(dstFull)) return Fail($"目标文件不存在: {dst}");

            try
            {
                var srcLines = File.ReadAllLines(srcFull);
                var dstLines = File.ReadAllLines(dstFull);

                var diffs = ComputeDiff(srcLines, dstLines, 100);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"对比 {src} ↔ {dst}:");
                sb.AppendLine($"源: {srcLines.Length} 行, 目标: {dstLines.Length} 行");

                var added = diffs.Count(d => d.Type == '+');
                var removed = diffs.Count(d => d.Type == '-');
                var changed = diffs.Count(d => d.Type == '~');
                sb.AppendLine($"差异: +{added} 增 / -{removed} 删 / ~{changed} 改");

                if (diffs.Count > 0)
                {
                    sb.AppendLine("---");
                    foreach (var d in diffs.Take(100))
                    {
                        sb.AppendLine($"{d.Type} L{d.SrcLine:D4}→L{d.DstLine:D4}: {d.Content}");
                    }
                    if (diffs.Count > 100)
                        sb.AppendLine($"... (结果已截断，共 {diffs.Count} 处差异)");
                }
                else
                {
                    sb.AppendLine("文件完全相同。");
                }

                return Ok(sb.ToString().TrimEnd());
            }
            catch (Exception ex) { return Fail($"对比失败: {ex.Message}"); }
        }

        private record DiffEntry(char Type, int SrcLine, int DstLine, string Content);

        private static List<DiffEntry> ComputeDiff(string[] src, string[] dst, int maxDiffs)
        {
            var diffs = new List<DiffEntry>();
            int i = 0, j = 0;

            while (i < src.Length && j < dst.Length)
            {
                if (diffs.Count >= maxDiffs) break;

                if (src[i] == dst[j])
                {
                    i++; j++;
                }
                else
                {
                    var found = false;
                    for (int look = 1; look <= 3 && (i + look < src.Length || j + look < dst.Length); look++)
                    {
                        if (j + look < dst.Length && src[i] == dst[j + look])
                        {
                            for (int k = 0; k < look; k++)
                                diffs.Add(new DiffEntry('+', i, j + k + 1, dst[j + k].Length > 200 ? dst[j + k][..200] + "..." : dst[j + k]));
                            j += look;
                            found = true;
                            break;
                        }
                        if (i + look < src.Length && src[i + look] == dst[j])
                        {
                            for (int k = 0; k < look; k++)
                                diffs.Add(new DiffEntry('-', i + k + 1, j, src[i + k].Length > 200 ? src[i + k][..200] + "..." : src[i + k]));
                            i += look;
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        var content = src[i].Length > 200 ? src[i][..200] + "..." : src[i];
                        diffs.Add(new DiffEntry('~', i + 1, j + 1, content));
                        i++; j++;
                    }
                }
            }

            while (i < src.Length && diffs.Count < maxDiffs)
            {
                diffs.Add(new DiffEntry('-', i + 1, dst.Length, src[i].Length > 200 ? src[i][..200] + "..." : src[i]));
                i++;
            }
            while (j < dst.Length && diffs.Count < maxDiffs)
            {
                diffs.Add(new DiffEntry('+', src.Length, j + 1, dst[j].Length > 200 ? dst[j][..200] + "..." : dst[j]));
                j++;
            }

            return diffs;
        }
    }
}
