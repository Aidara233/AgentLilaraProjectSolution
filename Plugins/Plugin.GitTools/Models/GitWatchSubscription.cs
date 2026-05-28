using System;
using System.Collections.Generic;

namespace Plugin.GitTools.Models;

public class GitWatchSubscription
{
    public string RepoName { get; set; } = "";
    public List<string> Events { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
