using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Logging;
using Plugin.GitTools.Models;
using Plugin.GitTools.Tools;

namespace Plugin.GitTools;

[Component(Name = "git-tools", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Disabled, Review = Applicability.Disabled, SubAgent = Applicability.Enabled)]
public class GitComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private ISignalLogger? _log;
    private string _instanceDir = "";
    private readonly List<GitWebhookEvent> _pendingEvents = new();

    private GitStatusTool? _statusTool;
    private GitLogTool? _logTool;
    private GitDiffTool? _diffTool;
    private GitCommitTool? _commitTool;
    private GitPushTool? _pushTool;
    private GitPullTool? _pullTool;
    private GitBranchTool? _branchTool;
    private GitCloneTool? _cloneTool;
    private GitInitTool? _initTool;
    private GitListReposTool? _listReposTool;
    private GitHubPrTool? _prTool;
    private GitHubIssueTool? _issueTool;
    private GitHubRepoTool? _repoTool;
    private GitHubWatchTool? _watchTool;
    private GitHubEventsTool? _eventsTool;

    public override ComponentMeta Meta => new()
    {
        Name = "git-tools",
        Description = "Git/GitHub 工具集：本地操作、仓库管理、API 调用、Webhook 订阅",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_statusTool != null) yield return _statusTool;
            if (_logTool != null) yield return _logTool;
            if (_diffTool != null) yield return _diffTool;
            if (_commitTool != null) yield return _commitTool;
            if (_pushTool != null) yield return _pushTool;
            if (_pullTool != null) yield return _pullTool;
            if (_branchTool != null) yield return _branchTool;
            if (_cloneTool != null) yield return _cloneTool;
            if (_initTool != null) yield return _initTool;
            if (_listReposTool != null) yield return _listReposTool;
            if (_prTool != null) yield return _prTool;
            if (_issueTool != null) yield return _issueTool;
            if (_repoTool != null) yield return _repoTool;
            if (_watchTool != null) yield return _watchTool;
            if (_eventsTool != null) yield return _eventsTool;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;
        _log = context.GetService<ISignalLogger>();
        _instanceDir = context.Storage.InstanceDirectory;

        var global = GitToolsAccessor.Global;
        if (global == null)
        {
            _log?.Warn("git", "global-not-available", new { loopId = context.LoopId });
            return Task.CompletedTask;
        }

        var workspace = global.WorkspaceDir;
        var runner = global.Runner;
        var repos = global.Repos;
        var gh = global.GitHub;

        _statusTool = new GitStatusTool(workspace, runner);
        _logTool = new GitLogTool(workspace, runner);
        _diffTool = new GitDiffTool(workspace, runner);
        _commitTool = new GitCommitTool(workspace, runner);
        _pushTool = new GitPushTool(workspace, runner);
        _pullTool = new GitPullTool(workspace, runner);
        _branchTool = new GitBranchTool(workspace, runner);
        _cloneTool = new GitCloneTool(workspace, runner, repos);
        _initTool = new GitInitTool(workspace, runner, repos);
        _listReposTool = new GitListReposTool(workspace, runner, repos);
        _prTool = new GitHubPrTool(workspace, runner, gh, repos);
        _issueTool = new GitHubIssueTool(workspace, runner, gh, repos);
        _repoTool = new GitHubRepoTool(workspace, runner, gh, repos);
        _watchTool = new GitHubWatchTool(workspace, runner, _instanceDir);
        _eventsTool = new GitHubEventsTool(workspace, runner, _instanceDir);

        _log?.Event("git", "loop-init", new
        {
            loopId = context.LoopId,
            gitAvailable = runner.IsAvailable,
            githubConfigured = gh.IsConfigured
        });

        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        _log?.Event("git", "loop-shutdown", new { reason = reason.ToString() });
        return Task.CompletedTask;
    }

    public override Task OnBeforeInvokeAsync()
    {
        DrainEvents();
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        if (_pendingEvents.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("[GitHub 事件]");
        foreach (var evt in _pendingEvents)
        {
            sb.AppendLine($"- {evt.EventType} on {evt.RepoName}: {evt.Title} (by {evt.Author})");
            if (!string.IsNullOrEmpty(evt.Url))
                sb.AppendLine($"  {evt.Url}");
        }
        sb.AppendLine("请处理以上 GitHub 事件。");

        _pendingEvents.Clear();
        return sb.ToString();
    }

    private void DrainEvents()
    {
        var eventsPath = Path.Combine(_instanceDir, "github_events.json");
        try
        {
            if (!File.Exists(eventsPath)) return;
            var json = File.ReadAllText(eventsPath);
            var events = JsonSerializer.Deserialize<List<GitWebhookEvent>>(json) ?? new List<GitWebhookEvent>();
            var unread = events.Where(e => !e.Read).ToList();
            if (unread.Count == 0) return;

            _pendingEvents.AddRange(unread);
            foreach (var e in unread) e.Read = true;

            var tmp = eventsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, eventsPath, overwrite: true);

            _log?.Event("git", "events-drained", new { count = unread.Count });
        }
        catch (Exception ex)
        {
            _log?.Warn("git", "drain-events-error", new { error = ex.Message });
        }
    }
}
