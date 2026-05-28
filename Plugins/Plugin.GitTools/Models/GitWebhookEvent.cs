using System;

namespace Plugin.GitTools.Models;

public class GitWebhookEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string EventType { get; set; } = "";
    public string RepoName { get; set; } = "";
    public string Action { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Author { get; set; } = "";
    public string RawSummary { get; set; } = "";
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
    public bool Read { get; set; } = false;
}
