using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using Plugin.GitTools.Core;
using Plugin.GitTools.Models;

namespace Plugin.GitTools.Tools;

[ToolMeta(Group = "github", ContinueLoop = true, CapabilitySummary = "管理 GitHub Webhook 订阅")]
public class GitHubWatchTool : GitToolBase
{
    private readonly string _instanceDir;

    public GitHubWatchTool(string workspaceDir, GitRunner runner, string instanceDir)
        : base(workspaceDir, runner)
    {
        _instanceDir = instanceDir;
    }

    public override string Name => "github_watch";
    public override string Description => "管理 GitHub Webhook 订阅。action: add(添加订阅) / remove(移除订阅) / list(查看订阅)。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("repo", "仓库名（如 agent-lilara）", 0),
        new("action", "操作类型: add / remove / list", 1),
        new("events", "（可选）逗号分隔的事件类型，如 push,pull_request,issues（add 需要）", 2, isRequired: false),
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var repo = Get(resolvedInputs, 0).Trim();
        var action = Get(resolvedInputs, 1).Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(repo)) return Task.FromResult(Fail("repo 不能为空"));
        if (string.IsNullOrEmpty(action)) return Task.FromResult(Fail("action 不能为空，可选: add / remove / list"));

        return action switch
        {
            "add" => DoAdd(repo, resolvedInputs),
            "remove" => DoRemove(repo, resolvedInputs),
            "list" => DoList(repo),
            _ => Task.FromResult(Fail($"未知 action: {action}，可选: add / remove / list"))
        };
    }

    private Task<ToolResult> DoAdd(string repo, List<string> inputs)
    {
        var eventsStr = Get(inputs, 2).Trim();
        var events = string.IsNullOrEmpty(eventsStr)
            ? new List<string> { "push", "pull_request" }
            : eventsStr.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).ToList();

        var watches = LoadWatches();
        var existing = watches.FirstOrDefault(w => string.Equals(w.RepoName, repo, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Events = events;
            existing.CreatedAt = DateTime.Now;
        }
        else
        {
            watches.Add(new GitWatchSubscription { RepoName = repo, Events = events, CreatedAt = DateTime.Now });
        }
        SaveWatches(watches);

        return Task.FromResult(Ok($"已订阅 [{repo}] 的事件: {string.Join(", ", events)}"));
    }

    private Task<ToolResult> DoRemove(string repo, List<string> inputs)
    {
        var watches = LoadWatches();
        var count = watches.RemoveAll(w => string.Equals(w.RepoName, repo, StringComparison.OrdinalIgnoreCase));
        SaveWatches(watches);

        if (count > 0) return Task.FromResult(Ok($"已取消 [{repo}] 的订阅"));
        return Task.FromResult(Ok($"[{repo}] 没有订阅记录"));
    }

    private Task<ToolResult> DoList(string repo)
    {
        var watches = LoadWatches();
        var matches = watches.Where(w => string.Equals(w.RepoName, repo, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return Task.FromResult(Ok($"[{repo}] 暂无订阅"));

        var sb = new StringBuilder();
        sb.AppendLine($"[{repo}] 订阅列表:");
        foreach (var w in matches)
        {
            sb.AppendLine($"  事件: {string.Join(", ", w.Events)}");
            sb.AppendLine($"  创建于: {w.CreatedAt:yyyy-MM-dd HH:mm}");
        }
        return Task.FromResult(Ok(sb.ToString().TrimEnd()));
    }

    private List<GitWatchSubscription> LoadWatches()
    {
        var path = Path.Combine(_instanceDir, "github_watches.json");
        try
        {
            if (!File.Exists(path)) return new List<GitWatchSubscription>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<GitWatchSubscription>>(json) ?? new List<GitWatchSubscription>();
        }
        catch { return new List<GitWatchSubscription>(); }
    }

    private void SaveWatches(List<GitWatchSubscription> watches)
    {
        Directory.CreateDirectory(_instanceDir);
        var path = Path.Combine(_instanceDir, "github_watches.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(watches, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
