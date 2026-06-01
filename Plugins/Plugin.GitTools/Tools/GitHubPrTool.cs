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

[ToolMeta(Group = "github", ContinueLoop = true, CapabilitySummary = "管理 GitHub Pull Requests")]
public class GitHubPrTool : GitToolBase
{
    private readonly GitHubClient _gh;
    private readonly RepoRegistry _repos;

    public GitHubPrTool(string workspaceDir, GitRunner runner, GitHubClient gh, RepoRegistry repos)
        : base(workspaceDir, runner)
    {
        _gh = gh;
        _repos = repos;
    }

    public override string Name => "github_pr";
    public override string Description => "管理 GitHub Pull Request。action: list(列出PR) / create(创建PR) / view(查看PR详情) / merge(合并PR)。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("repo", "仓库路径（相对于 Workspace 目录）", 0),
        new("action", "操作类型: list / create / view / merge", 1),
        new("number", "（可选）PR 编号（view/merge 需要）", 2, isRequired: false),
        new("title", "（可选）PR 标题（create 需要）", 3, isRequired: false),
        new("body", "（可选）PR 描述（create 需要）", 4, isRequired: false),
        new("head", "（可选）源分支（create 需要）", 5, isRequired: false),
        new("base", "（可选）目标分支（create 需要）", 6, isRequired: false),
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
        if (owner == null || repoName == null) return Fail($"无法确定仓库 {repo} 的 GitHub owner/repo。请确保仓库已注册或 remote URL 包含 github.com。");

        return action switch
        {
            "list" => await DoList(owner, repoName, resolvedInputs, ct),
            "create" => await DoCreate(owner, repoName, resolvedInputs, ct),
            "view" => await DoView(owner, repoName, resolvedInputs, ct),
            "merge" => await DoMerge(owner, repoName, resolvedInputs, ct),
            _ => Fail($"未知 action: {action}，可选: list / create / view / merge")
        };
    }

    private async Task<ToolResult> DoList(string owner, string repo, List<string> inputs, CancellationToken ct)
    {
        var state = Get(inputs, 2).Trim();
        if (string.IsNullOrEmpty(state)) state = "open";
        var json = await _gh.ListPullRequests(owner, repo, state, ct);
        if (json.StartsWith("GitHub API")) return Fail(json);

        var sb = new StringBuilder();
        sb.AppendLine($"[{owner}/{repo}] PR 列表 (state={state}):");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var num = item.GetProperty("number").GetInt32();
                    var title = item.GetProperty("title").GetString() ?? "";
                    var user = item.GetProperty("user").GetProperty("login").GetString() ?? "";
                    var stateVal = item.GetProperty("state").GetString() ?? "";
                    sb.AppendLine($"  #{num} [{stateVal}] {title} (by {user})");
                }
            }
        }
        catch { sb.AppendLine(json); }
        return Ok(sb.ToString().TrimEnd());
    }

    private async Task<ToolResult> DoCreate(string owner, string repo, List<string> inputs, CancellationToken ct)
    {
        var title = Get(inputs, 3).Trim();
        var body = Get(inputs, 4).Trim();
        var head = Get(inputs, 5).Trim();
        var baseBranch = Get(inputs, 6).Trim();

        if (string.IsNullOrEmpty(title)) return Fail("create 需要 title 参数");
        if (string.IsNullOrEmpty(head)) return Fail("create 需要 head 参数（源分支）");
        if (string.IsNullOrEmpty(baseBranch)) baseBranch = "main";

        var json = await _gh.CreatePullRequest(owner, repo, title, body, head, baseBranch, ct);
        if (json.StartsWith("GitHub API")) return Fail(json);
        return Ok($"[{owner}/{repo}] PR 创建成功:\n{FormatPrResponse(json)}");
    }

    private async Task<ToolResult> DoView(string owner, string repo, List<string> inputs, CancellationToken ct)
    {
        var numStr = Get(inputs, 2).Trim();
        if (!int.TryParse(numStr, out var number)) return Fail("view 需要 number 参数（PR 编号）");

        var json = await _gh.GetPullRequest(owner, repo, number, ct);
        if (json.StartsWith("GitHub API")) return Fail(json);
        return Ok($"[{owner}/{repo}] PR #{number}:\n{FormatPrResponse(json)}");
    }

    private async Task<ToolResult> DoMerge(string owner, string repo, List<string> inputs, CancellationToken ct)
    {
        var numStr = Get(inputs, 2).Trim();
        if (!int.TryParse(numStr, out var number)) return Fail("merge 需要 number 参数（PR 编号）");

        var json = await _gh.MergePullRequest(owner, repo, number, ct);
        if (json.StartsWith("GitHub API")) return Fail(json);
        return Ok($"[{owner}/{repo}] PR #{number} 已合并");
    }

    private (string? owner, string? repo) ResolveGitHubRepo(string repoPath)
    {
        var entry = _repos.GetByName(repoPath) ?? _repos.GetByPath(repoPath);
        if (entry?.GitHubOwner != null) return (entry.GitHubOwner, entry.GitHubRepo);

        // Fallback: try to parse from remote URL via git
        var fullPath = ResolveRepoPath(repoPath);
        if (fullPath != null && IsGitRepo(fullPath))
        {
            var result = Runner.RunAsync(fullPath, "remote get-url origin").Result;
            if (result.Success)
            {
                var url = result.Output.Trim();
                var match = System.Text.RegularExpressions.Regex.Match(url, @"github\.com[:/]([^/]+)/([^/]+?)(\.git)?");
                if (match.Success) return (match.Groups[1].Value, match.Groups[2].Value);
            }
        }
        return (null, null);
    }

    private static string FormatPrResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var num = root.GetProperty("number").GetInt32();
            var title = root.GetProperty("title").GetString() ?? "";
            var state = root.GetProperty("state").GetString() ?? "";
            var url = root.GetProperty("html_url").GetString() ?? "";
            var user = root.GetProperty("user").GetProperty("login").GetString() ?? "";
            return $"#{num} [{state}] {title}\n作者: {user}\n链接: {url}";
        }
        catch { return json[..Math.Min(500, json.Length)]; }
    }
}
