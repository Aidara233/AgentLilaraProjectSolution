using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Logging;
using Plugin.GitTools.Config;
using Plugin.GitTools.Core;
using Plugin.GitTools.Models;
using Plugin.GitTools.Webhook;

namespace Plugin.GitTools;

[Component(Name = "git-tools-global", Scope = ComponentScope.Global)]
public class GitGlobalComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private ISignalLogger? _log;
    private WebhookListener? _webhookListener;
    private string _pluginDataRoot = "";

    public GitHubConfig Config { get; private set; } = new();
    public RepoRegistry Repos { get; private set; } = null!;
    public GitRunner Runner { get; private set; } = null!;
    public GitHubClient GitHub { get; private set; } = null!;
    public string WorkspaceDir { get; private set; } = "";

    public override ComponentMeta Meta => new()
    {
        Name = "git-tools-global",
        Description = "Git/GitHub 全局组件：配置、仓库注册、GitRunner、Webhook 监听",
        DefaultEnabled = true,
        PromptPriority = 99
    };

    public override IEnumerable<ITool> Tools => Array.Empty<ITool>();

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;
        _log = context.GetService<ISignalLogger>();

        WorkspaceDir = context.Storage.WorkspaceDirectory;
        _pluginDataRoot = Path.GetFullPath(Path.Combine(context.Storage.GlobalDirectory, ".."));

        Config = GitHubConfig.Load(context.Storage.GlobalDirectory);
        Repos = new RepoRegistry(context.Storage.GlobalDirectory);
        Runner = new GitRunner();
        GitHub = new GitHubClient(Config.Token);

        _log?.Event("git", "global-init", new
        {
            workspace = WorkspaceDir,
            gitAvailable = Runner.IsAvailable,
            tokenConfigured = !string.IsNullOrEmpty(Config.Token),
            webhookPort = Config.WebhookPort
        });

        StartWebhookListener();

        GitToolsAccessor.Configure(this);

        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        _log?.Event("git", "global-shutdown", new { reason = reason.ToString() });
        _webhookListener?.Dispose();
        _webhookListener = null;
        GitToolsAccessor.Clear();
        return Task.CompletedTask;
    }

    private void StartWebhookListener()
    {
        if (Config.WebhookPort <= 0) return;

        try
        {
            _webhookListener = new WebhookListener(Config.WebhookPort, Config.WebhookSecret, _log);
            _webhookListener.OnEventReceived = DispatchWebhookEvent;
            _webhookListener.Start();
        }
        catch (Exception ex)
        {
            _log?.Warn("git", "webhook-listener-failed", new { error = ex.Message });
            _webhookListener = null;
        }
    }

    private void DispatchWebhookEvent(string eventType, string rawBody)
    {
        try
        {
            var evt = ParseWebhookEvent(eventType, rawBody);
            if (evt == null) return;

            _log?.Event("git-webhook", "dispatching", new { eventType, repo = evt.RepoName, action = evt.Action });

            // Scan all loop instance directories for matching subscriptions
            if (!Directory.Exists(_pluginDataRoot)) return;

            foreach (var loopDir in Directory.GetDirectories(_pluginDataRoot))
            {
                var watchesPath = Path.Combine(loopDir, "github_watches.json");
                if (!File.Exists(watchesPath)) continue;

                try
                {
                    var json = File.ReadAllText(watchesPath);
                    var watches = JsonSerializer.Deserialize<List<GitWatchSubscription>>(json) ?? new List<GitWatchSubscription>();
                    var match = watches.FirstOrDefault(w =>
                        string.Equals(w.RepoName, evt.RepoName, StringComparison.OrdinalIgnoreCase) &&
                        w.Events.Contains(eventType, StringComparer.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        var loopId = Path.GetFileName(loopDir).Replace('_', ':');
                        EnqueueEvent(loopDir, evt);
                        _ctx.WakeLoop(loopId);
                        _log?.Event("git-webhook", "loop-woken", new { loopId, eventType });
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warn("git-webhook", "scan-loop-error", new { loopDir, error = ex.Message });
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Error("git-webhook", "dispatch-error", new { error = ex.Message });
        }
    }

    private void EnqueueEvent(string loopDir, GitWebhookEvent evt)
    {
        var eventsPath = Path.Combine(loopDir, "github_events.json");
        List<GitWebhookEvent> events;
        try
        {
            var json = File.Exists(eventsPath) ? File.ReadAllText(eventsPath) : "[]";
            events = JsonSerializer.Deserialize<List<GitWebhookEvent>>(json) ?? new List<GitWebhookEvent>();
        }
        catch { events = new List<GitWebhookEvent>(); }

        events.Add(evt);
        // Keep only last 50 events
        if (events.Count > 50) events = events.TakeLast(50).ToList();

        Directory.CreateDirectory(loopDir);
        var tmp = eventsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, eventsPath, overwrite: true);
    }

    private static GitWebhookEvent? ParseWebhookEvent(string eventType, string rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;

            var repoName = "";
            if (root.TryGetProperty("repository", out var repo))
            {
                repoName = repo.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
            }

            var action = root.TryGetProperty("action", out var act) ? act.GetString() ?? "" : "";
            var author = "";
            var title = "";
            var url = "";

            if (root.TryGetProperty("sender", out var sender) && sender.TryGetProperty("login", out var login))
                author = login.GetString() ?? "";

            switch (eventType)
            {
                case "push":
                    if (root.TryGetProperty("commits", out var commits) && commits.GetArrayLength() > 0)
                    {
                        var first = commits[0];
                        title = $"{commits.GetArrayLength()} new commit(s)";
                        if (first.TryGetProperty("message", out var msg)) title = msg.GetString() ?? title;
                    }
                    if (root.TryGetProperty("compare", out var compare)) url = compare.GetString() ?? "";
                    break;
                case "pull_request":
                    if (root.TryGetProperty("pull_request", out var pr))
                    {
                        if (pr.TryGetProperty("title", out var prTitle)) title = prTitle.GetString() ?? "";
                        if (pr.TryGetProperty("html_url", out var prUrl)) url = prUrl.GetString() ?? "";
                    }
                    break;
                case "issues":
                    if (root.TryGetProperty("issue", out var issue))
                    {
                        if (issue.TryGetProperty("title", out var issueTitle)) title = issueTitle.GetString() ?? "";
                        if (issue.TryGetProperty("html_url", out var issueUrl)) url = issueUrl.GetString() ?? "";
                    }
                    break;
                default:
                    title = $"{eventType} event";
                    break;
            }

            return new GitWebhookEvent
            {
                EventType = eventType,
                RepoName = repoName,
                Action = action,
                Title = title,
                Url = url,
                Author = author,
                RawSummary = rawBody[..Math.Min(500, rawBody.Length)],
                ReceivedAt = DateTime.Now
            };
        }
        catch
        {
            return null;
        }
    }
}
