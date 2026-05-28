using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using Plugin.GitTools.Config;
using Plugin.GitTools.Core;

namespace Plugin.GitTools.Tools;

[ToolMeta(Group = "github", ContinueLoop = true, CapabilitySummary = "查询 GitHub 仓库信息")]
public class GitHubRepoTool : GitToolBase
{
    private readonly GitHubClient _gh;
    private readonly RepoRegistry _repos;

    public GitHubRepoTool(string workspaceDir, GitRunner runner, GitHubClient gh, RepoRegistry repos)
        : base(workspaceDir, runner)
    {
        _gh = gh;
        _repos = repos;
    }

    public override string Name => "github_repo";
    public override string Description => "查询 GitHub 仓库信息。action: info(仓库详情) / branches(分支列表) / tags(标签列表)。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("repo", "仓库路径（相对于 Workspace 目录）", 0),
        new("action", "操作类型: info / branches / tags", 1),
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public override async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var repo = Get(resolvedInputs, 0).Trim();
        var action = Get(resolvedInputs, 1).Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(repo)) return Fail("repo 不能为空");
        if (string.IsNullOrEmpty(action)) return Fail("action 不能为空");
        if (!_gh.IsConfigured) return Fail("GitHub token 未配置。请在 GitHubConfig.json 中设置 token。");

        var (owner, repoName) = ResolveGitHubRepo(repo);
        if (owner == null) return Fail($"无法确定仓库 {repo} 的 GitHub owner/repo。");

        return action switch
        {
            "info" => await DoInfo(owner, repoName, ct),
            "branches" => await DoBranches(owner, repoName, ct),
            "tags" => await DoTags(owner, repoName, ct),
            _ => Fail($"未知 action: {action}，可选: info / branches / tags")
        };
    }

    private async Task<ToolResult> DoInfo(string owner, string repo, CancellationToken ct)
    {
        var json = await _gh.GetRepoInfo(owner, repo, ct);
        if (json.StartsWith("GitHub API")) return Fail(json);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var sb = new StringBuilder();
            sb.AppendLine($"[{owner}/{repo}]");
            sb.AppendLine($"  描述: {r.GetProperty("description").GetString() ?? "(无)"}");
            sb.AppendLine($"  语言: {r.GetProperty("language").GetString() ?? "(未知)"}");
            sb.AppendLine($"  Stars: {r.GetProperty("stargazers_count").GetInt32()}");
            sb.AppendLine($"  Forks: {r.GetProperty("forks_count").GetInt32()}");
            sb.AppendLine($"  默认分支: {r.GetProperty("default_branch").GetString()}");
            sb.AppendLine($"  链接: {r.GetProperty("html_url").GetString()}");
            return Ok(sb.ToString().TrimEnd());
        }
        catch { return Ok(json[..Math.Min(1000, json.Length)]); }
    }

    private async Task<ToolResult> DoBranches(string owner, string repo, CancellationToken ct)
    {
        var json = await _gh.ListBranches(owner, repo, ct);
        if (json.StartsWith("GitHub API")) return Fail(json);

        var sb = new StringBuilder();
        sb.AppendLine($"[{owner}/{repo}] 分支列表:");
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                sb.AppendLine($"  {name}");
            }
        }
        catch { sb.AppendLine(json); }
        return Ok(sb.ToString().TrimEnd());
    }

    private async Task<ToolResult> DoTags(string owner, string repo, CancellationToken ct)
    {
        var json = await _gh.ListTags(owner, repo, ct);
        if (json.StartsWith("GitHub API")) return Fail(json);

        var sb = new StringBuilder();
        sb.AppendLine($"[{owner}/{repo}] 标签列表:");
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                sb.AppendLine($"  {name}");
            }
        }
        catch { sb.AppendLine(json); }
        return Ok(sb.ToString().TrimEnd());
    }

    private (string? owner, string? repo) ResolveGitHubRepo(string repoPath)
    {
        var entry = _repos.GetByName(repoPath) ?? _repos.GetByPath(repoPath);
        if (entry?.GitHubOwner != null) return (entry.GitHubOwner, entry.GitHubRepo);

        var fullPath = ResolveRepoPath(repoPath);
        if (fullPath != null && IsGitRepo(fullPath))
        {
            var result = Runner.RunAsync(fullPath, "remote get-url origin").Result;
            if (result.Success)
            {
                var url = result.Output.Trim();
                var match = System.Text.RegularExpressions.Regex.Match(url, @"github\.com[:/]([^/]+)/([^/]+?)(\.git)?$");
                if (match.Success) return (match.Groups[1].Value, match.Groups[2].Value);
            }
        }
        return (null, null);
    }
}
