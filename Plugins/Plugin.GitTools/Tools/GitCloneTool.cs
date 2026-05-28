using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using Plugin.GitTools.Config;
using Plugin.GitTools.Core;

namespace Plugin.GitTools.Tools;

[ToolMeta(Group = "git", ContinueLoop = true, CapabilitySummary = "克隆 git 仓库到 Workspace")]
public class GitCloneTool : GitToolBase
{
    private readonly RepoRegistry _repos;

    public GitCloneTool(string workspaceDir, GitRunner runner, RepoRegistry repos) : base(workspaceDir, runner)
    {
        _repos = repos;
    }

    public override string Name => "git_clone";
    public override string Description => "克隆 git 仓库到 Workspace 目录。支持自动从 URL 提取仓库名。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("url", "仓库 URL（https:// 或 git@ 格式）", 0),
        new("path", "（可选）目标路径（相对于 Workspace），默认从 URL 自动提取仓库名", 1, isRequired: false),
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

    public override async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var url = Get(resolvedInputs, 0).Trim();
        var path = Get(resolvedInputs, 1).Trim();

        if (string.IsNullOrEmpty(url))
            return Fail("url 不能为空");

        if (string.IsNullOrEmpty(path))
        {
            path = ExtractRepoName(url);
            if (string.IsNullOrEmpty(path))
                return Fail("无法从 URL 提取仓库名，请手动指定 path 参数");
        }

        var fullPath = ResolveRepoPath(path);
        if (fullPath == null)
            return Fail("路径不合法：不能访问 Workspace 目录之外");
        if (Directory.Exists(fullPath) && IsGitRepo(fullPath))
            return Fail($"目录已是 git 仓库: {path}");

        var result = await Runner.RunAsync(WorkspaceDir, $"clone {url} {path}", 120, ct);
        var output = (result.Output + "\n" + result.Error).Trim();

        if (!result.Success)
            return Fail($"git clone 失败: {output}");

        var (owner, repoName) = ParseGitHubUrl(url);
        var entry = new RepoEntry
        {
            Name = path,
            RelativePath = path,
            RemoteUrl = url,
            GitHubOwner = owner,
            GitHubRepo = repoName,
            RegisteredAt = DateTime.Now
        };
        _repos.Register(entry);

        var githubInfo = owner != null ? $"\nGitHub: {owner}/{repoName}" : "";
        return Ok($"[{path}] 克隆成功{githubInfo}\n{output}");
    }

    private static string ExtractRepoName(string url)
    {
        url = url.TrimEnd('/');
        if (url.EndsWith(".git"))
            url = url[..^4];
        var lastSlash = url.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < url.Length - 1)
            return url[(lastSlash + 1)..];
        return "";
    }

    private static (string? owner, string? repo) ParseGitHubUrl(string url)
    {
        var match = Regex.Match(url, @"github\.com[:/]([^/]+)/([^/]+?)(\.git)?$");
        if (match.Success)
            return (match.Groups[1].Value, match.Groups[2].Value);
        return (null, null);
    }
}
