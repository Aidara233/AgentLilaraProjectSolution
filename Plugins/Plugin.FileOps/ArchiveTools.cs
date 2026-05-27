// Plugins/Plugin.FileOps/ArchiveTools.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using FileToolKit.Shared;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace Plugin.FileOps
{
    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "创建压缩归档")]
    public class ArchiveCreateTool : FileToolBase
    {
        public ArchiveCreateTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "archive_create";
        public override string Description => "将文件或目录打包压缩。format 从目标扩展名自动推断（zip/tar.gz/7z），也可手动指定。level: fast/balanced/best。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "要压缩的文件或目录路径", 0),
            new("output", "输出归档文件路径", 1),
            new("format", "（可选）zip / tar.gz / 7z，默认从 output 扩展名推断", 2, isRequired: false),
            new("level", "（可选）fast / balanced / best，默认 balanced", 3, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var output = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
            var format = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim().ToLower() : "";
            var levelStr = resolvedInputs.Count > 3 ? resolvedInputs[3].Trim().ToLower() : "";

            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(output))
                return Fail("path 和 output 都不能为空");

            if (string.IsNullOrEmpty(format)) format = DetectFormat(output);
            var compressionLevel = levelStr switch
            {
                "fast" => System.IO.Compression.CompressionLevel.Fastest,
                "best" => System.IO.Compression.CompressionLevel.Optimal,
                _ => System.IO.Compression.CompressionLevel.Optimal
            };

            var srcFull = ResolvePath(path);
            var dstFull = ResolvePath(output);
            if (srcFull == null || dstFull == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!File.Exists(srcFull) && !Directory.Exists(srcFull))
                return Fail($"源不存在: {path}");

            try
            {
                var dstDir = Path.GetDirectoryName(dstFull)!;
                Directory.CreateDirectory(dstDir);

                switch (format)
                {
                    case "zip":
                        if (File.Exists(srcFull))
                        {
                            using var archive = ZipFile.Open(dstFull, ZipArchiveMode.Create);
                            archive.CreateEntryFromFile(srcFull, Path.GetFileName(srcFull), compressionLevel);
                        }
                        else
                        {
                            ZipFile.CreateFromDirectory(srcFull, dstFull, compressionLevel, includeBaseDirectory: true);
                        }
                        break;

                    case "tar":
                        {
                            using var fs = File.Create(dstFull);
                            using var writer = new System.Formats.Tar.TarWriter(fs, leaveOpen: false);
                            AddToTar(writer, srcFull, Path.GetFileName(srcFull), ct);
                        }
                        break;

                    case "tar.gz":
                    case "tgz":
                        {
                            using var fs = File.Create(dstFull);
                            using var gz = new GZipStream(fs, compressionLevel);
                            using var writer = new System.Formats.Tar.TarWriter(gz, leaveOpen: false);
                            AddToTar(writer, srcFull, Path.GetFileName(srcFull), ct);
                        }
                        break;

                    case "7z":
                        {
                            using var fs = File.Create(dstFull);
                            using var writer = WriterFactory.Open(fs, ArchiveType.SevenZip, new WriterOptions(CompressionType.LZMA));
                            if (File.Exists(srcFull))
                            {
                                using var fileStream = File.OpenRead(srcFull);
                                writer.Write(Path.GetFileName(srcFull), fileStream, null);
                            }
                            else
                            {
                                foreach (var file in Directory.GetFiles(srcFull, "*", SearchOption.AllDirectories))
                                {
                                    ct.ThrowIfCancellationRequested();
                                    var relativePath = Path.GetRelativePath(srcFull, file);
                                    using var fileStream = File.OpenRead(file);
                                    writer.Write(relativePath, fileStream, null);
                                }
                            }
                        }
                        break;

                    default:
                        return Fail($"不支持的格式: {format}。支持 zip / tar / tar.gz / 7z");
                }

                var size = new FileInfo(dstFull).Length;
                return Ok($"已创建 {format} 归档: {output} ({FormatSize(size)})");
            }
            catch (Exception ex) { return Fail($"创建归档失败: {ex.Message}"); }
        }

        private static void AddToTar(System.Formats.Tar.TarWriter writer, string sourcePath, string entryName, CancellationToken ct)
        {
            if (File.Exists(sourcePath))
            {
                writer.WriteEntry(sourcePath, entryName);
            }
            else if (Directory.Exists(sourcePath))
            {
                foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var relativePath = Path.GetRelativePath(sourcePath, file);
                    writer.WriteEntry(file, Path.Combine(entryName, relativePath));
                }
            }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "解压归档文件")]
    public class ArchiveExtractTool : FileToolBase
    {
        public ArchiveExtractTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "archive_extract";
        public override string Description => "解压归档文件到指定目录。支持 zip/tar/tar.gz/7z 格式。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("source", "归档文件路径", 0),
            new("target_dir", "（可选）解压目标目录，默认为归档文件所在目录下同名文件夹", 1, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var source = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var targetDir = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

            if (string.IsNullOrEmpty(source))
                return Fail("source 不能为空");

            var srcFull = ResolvePath(source);
            if (srcFull == null) return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!File.Exists(srcFull)) return Fail($"归档文件不存在: {source}");

            if (string.IsNullOrEmpty(targetDir))
                targetDir = Path.GetFileNameWithoutExtension(source);
            var targetFull = ResolvePath(targetDir);
            if (targetFull == null) return Fail("目标路径不合法");

            try
            {
                Directory.CreateDirectory(targetFull);
                var format = DetectFormat(source);

                switch (format)
                {
                    case "zip":
                        ZipFile.ExtractToDirectory(srcFull, targetFull, overwriteFiles: true);
                        break;

                    case "tar":
                        {
                            using var fs = File.OpenRead(srcFull);
                            System.Formats.Tar.TarFile.ExtractToDirectory(fs, targetFull, overwriteFiles: true);
                        }
                        break;

                    case "tar.gz":
                    case "tgz":
                        {
                            using var fs = File.OpenRead(srcFull);
                            using var gz = new GZipStream(fs, CompressionMode.Decompress);
                            System.Formats.Tar.TarFile.ExtractToDirectory(gz, targetFull, overwriteFiles: true);
                        }
                        break;

                    case "7z":
                        {
                            using var archive = SevenZipArchive.Open(srcFull);
                            foreach (var entry in archive.Entries)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (!entry.IsDirectory)
                                {
                                    var destPath = Path.GetFullPath(Path.Combine(targetFull, entry.Key ?? ""));
                                    if (!destPath.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
                                        continue;
                                    entry.WriteToDirectory(targetFull, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                                }
                            }
                        }
                        break;

                    default:
                        return Fail($"无法识别归档格式: {source}");
                }

                var count = Directory.GetFileSystemEntries(targetFull).Length;
                return Ok($"已解压 {source} → {targetDir}/ ({count} 个条目)");
            }
            catch (Exception ex) { return Fail($"解压失败: {ex.Message}"); }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "列出归档内容")]
    public class ArchiveListTool : FileToolBase
    {
        public ArchiveListTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "archive_list";
        public override string Description => "列出压缩包内容（不实际解压）。最多显示 100 个条目。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("source", "归档文件路径", 0)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var source = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            if (string.IsNullOrEmpty(source)) return Fail("source 不能为空");

            var srcFull = ResolvePath(source);
            if (srcFull == null) return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!File.Exists(srcFull)) return Fail($"归档文件不存在: {source}");

            try
            {
                var format = DetectFormat(source);
                var sb = new System.Text.StringBuilder();
                var count = 0;
                const int maxEntries = 100;

                switch (format)
                {
                    case "zip":
                        using (var archive = ZipFile.OpenRead(srcFull))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                if (count >= maxEntries) break;
                                sb.AppendLine(entry.FullName + (entry.Length > 0 ? $" ({FormatSize(entry.Length)})" : ""));
                                count++;
                            }
                        }
                        break;

                    case "tar":
                    case "tar.gz":
                    case "tgz":
                        {
                            using var fs = File.OpenRead(srcFull);
                            Stream stream = fs;
                            if (format is "tar.gz" or "tgz")
                                stream = new GZipStream(fs, CompressionMode.Decompress);
                            using var reader = new System.Formats.Tar.TarReader(stream, leaveOpen: false);
                            while (reader.GetNextEntry() is { } entry)
                            {
                                if (count >= maxEntries) break;
                                sb.AppendLine($"{entry.Name} ({FormatSize(entry.Length)}) [{entry.EntryType}]");
                                count++;
                            }
                        }
                        break;

                    case "7z":
                        using (var archive = SevenZipArchive.Open(srcFull))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                if (count >= maxEntries) break;
                                sb.AppendLine(entry.Key + (entry.Size > 0 ? $" ({FormatSize((long)entry.Size)})" : ""));
                                count++;
                            }
                        }
                        break;

                    default:
                        return Fail($"无法识别归档格式: {source}");
                }

                var result = count >= maxEntries
                    ? sb.ToString().TrimEnd() + $"\n... (结果已截断，共 {count} 条目)"
                    : sb.ToString().TrimEnd();
                return Ok(result.Length > 0 ? result : "(空归档)");
            }
            catch (Exception ex) { return Fail($"列出归档失败: {ex.Message}"); }
        }
    }
}
