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

[ToolMeta(Group = "github", ContinueLoop = true, CapabilitySummary = "查看 GitHub Webhook 事件")]
public class GitHubEventsTool : GitToolBase
{
    private readonly string _instanceDir;

    public GitHubEventsTool(string workspaceDir, GitRunner runner, string instanceDir)
        : base(workspaceDir, runner)
    {
        _instanceDir = instanceDir;
    }

    public override string Name => "github_events";
    public override string Description => "查看或清理 GitHub Webhook 事件队列。action: list(查看未读事件) / clear(清空事件)。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("action", "操作类型: list / clear", 0),
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var action = Get(resolvedInputs, 0).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(action)) return Task.FromResult(Fail("action 不能为空，可选: list / clear"));

        return action switch
        {
            "list" => DoList(),
            "clear" => DoClear(),
            _ => Task.FromResult(Fail($"未知 action: {action}，可选: list / clear"))
        };
    }

    private Task<ToolResult> DoList()
    {
        var events = LoadEvents();
        var unread = events.Where(e => !e.Read).ToList();

        if (unread.Count == 0)
            return Task.FromResult(Ok("暂无未读的 GitHub 事件。"));

        var sb = new StringBuilder();
        sb.AppendLine($"[GitHub 事件] 共 {unread.Count} 条未读:");
        foreach (var evt in unread)
        {
            sb.AppendLine($"  [{evt.EventType}] {evt.RepoName}: {evt.Title} (by {evt.Author})");
            if (!string.IsNullOrEmpty(evt.Url)) sb.AppendLine($"    链接: {evt.Url}");
            if (!string.IsNullOrEmpty(evt.RawSummary)) sb.AppendLine($"    摘要: {evt.RawSummary[..Math.Min(200, evt.RawSummary.Length)]}");
            evt.Read = true;
        }
        SaveEvents(events);

        return Task.FromResult(Ok(sb.ToString().TrimEnd()));
    }

    private Task<ToolResult> DoClear()
    {
        SaveEvents(new List<GitWebhookEvent>());
        return Task.FromResult(Ok("已清空 GitHub 事件队列。"));
    }

    private List<GitWebhookEvent> LoadEvents()
    {
        var path = Path.Combine(_instanceDir, "github_events.json");
        try
        {
            if (!File.Exists(path)) return new List<GitWebhookEvent>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<GitWebhookEvent>>(json) ?? new List<GitWebhookEvent>();
        }
        catch { return new List<GitWebhookEvent>(); }
    }

    private void SaveEvents(List<GitWebhookEvent> events)
    {
        Directory.CreateDirectory(_instanceDir);
        var path = Path.Combine(_instanceDir, "github_events.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
