// Plugins/Plugin.FileOps/SearchTools.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using FileToolKit.Shared;

namespace Plugin.FileOps
{
    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "按文件名模式搜索文件")]
    public class SearchFilesTool : FileToolBase
    {
        public SearchFilesTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "search_files";
        public override string Description => "使用 glob 模式搜索文件（如 **/*.cs, *.txt, logs/*.json）。最多返回 100 个结果。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("pattern", "glob 模式，如 **/*.cs", 0),
            new("dir", "（可选）搜索起始目录，默认 Workspace 根目录", 1, isRequired: false),
            new("recursive", "（可选）递归搜索，默认 true", 2, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var pattern = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var dir = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
            var recursive = resolvedInputs.Count <= 2 || !resolvedInputs[2].Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(pattern))
                return Fail("pattern 不能为空");

            var baseDir = string.IsNullOrEmpty(dir) ? WorkspaceDir : ResolvePath(dir);
            if (baseDir == null) return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!Directory.Exists(baseDir)) return Fail($"目录不存在: {dir}");

            try
            {
                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var globPattern = ConvertGlobToRegex(pattern);

                var allFiles = Directory.GetFiles(baseDir, "*", searchOption);
                var sb = new System.Text.StringBuilder();
                var count = 0;
                const int maxResults = 100;

                foreach (var file in allFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(WorkspaceDir, file);
                    if (globPattern.IsMatch(relative) || globPattern.IsMatch(Path.GetFileName(file)))
                    {
                        if (count >= maxResults) break;
                        var info = new FileInfo(file);
                        sb.AppendLine($"{relative} ({FormatSize(info.Length)})");
                        count++;
                    }
                }

                sb.Insert(0, $"搜索 '{pattern}' 结果 ({count} 个文件):\n");
                if (count >= maxResults)
                    sb.AppendLine($"... (结果已截断，共 {count} 文件)");
                return Ok(sb.ToString().TrimEnd());
            }
            catch (Exception ex) { return Fail($"搜索失败: {ex.Message}"); }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "在文件内容中搜索文本")]
    public class GrepFilesTool : FileToolBase
    {
        public GrepFilesTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "grep_files";
        public override string Description => "在目录中搜索文件内容（支持正则），返回匹配行的文件路径和行号。最多返回 30 条，每条截断到 500 字符。建议先用本工具定位到具体文件和行号，再用 read_text 按行号范围读取上下文。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("pattern", "搜索模式（正则表达式）", 0),
            new("dir", "（可选）搜索起始目录，默认 Workspace 根目录", 1, isRequired: false),
            new("file_pattern", "（可选）glob 过滤文件名，如 *.cs", 2, isRequired: false),
            new("max_results", "（可选）最大结果数，默认 30", 3, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var pattern = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var dir = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
            var filePattern = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";
            var maxStr = resolvedInputs.Count > 3 ? resolvedInputs[3].Trim() : "30";

            if (string.IsNullOrEmpty(pattern))
                return Fail("pattern 不能为空");
            if (!int.TryParse(maxStr, out var maxResults)) maxResults = 30;
            maxResults = Math.Min(maxResults, 30);

            var baseDir = string.IsNullOrEmpty(dir) ? WorkspaceDir : ResolvePath(dir);
            if (baseDir == null) return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!Directory.Exists(baseDir)) return Fail($"目录不存在: {dir}");

            try
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled,
                    TimeSpan.FromSeconds(5));
                var fileRegex = string.IsNullOrEmpty(filePattern) ? null
                    : ConvertGlobToRegex(filePattern);

                var files = Directory.GetFiles(baseDir, "*", SearchOption.AllDirectories);
                var sb = new System.Text.StringBuilder();
                var totalCount = 0;

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (totalCount >= maxResults) break;

                    if (fileRegex != null)
                    {
                        var relative = Path.GetRelativePath(WorkspaceDir, file);
                        if (!fileRegex.IsMatch(relative)) continue;
                    }

                    var info = new FileInfo(file);
                    if (info.Length > 10 * 1024 * 1024) continue;

                    try
                    {
                        using var reader = new StreamReader(file);
                        string? line;
                        int lineNum = 0;
                        while ((line = reader.ReadLine()) != null && totalCount < maxResults)
                        {
                            ct.ThrowIfCancellationRequested();
                            lineNum++;
                            var match = regex.Match(line);
                            if (match.Success)
                            {
                                var relative = Path.GetRelativePath(WorkspaceDir, file);
                                var display = line.Length > 500 ? line[..500] + "..." : line;
                                sb.AppendLine($"{relative}:{lineNum}: {display}");
                                totalCount++;
                            }
                        }
                    }
                    catch (IOException) { continue; }
                }

                sb.Insert(0, $"grep '{pattern}' 结果 ({totalCount} 条匹配):\n");
                if (totalCount >= maxResults)
                    sb.AppendLine($"... (结果已截断，共 {totalCount} 条匹配)");
                return Ok(sb.ToString().TrimEnd());
            }
            catch (RegexMatchTimeoutException) { return Fail("正则匹配超时，请简化表达式"); }
            catch (Exception ex) { return Fail($"搜索失败: {ex.Message}"); }
        }
    }
}
