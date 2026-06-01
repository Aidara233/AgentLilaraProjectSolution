using System;
using System.IO;
using System.Text.Json;

namespace Plugin.GitTools.Config;

public class GitHubConfig
{
    public string Token { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public int WebhookPort { get; set; } = 23456;
    public string WebhookBaseUrl { get; set; } = "";
    public string CloneProxy { get; set; } = "g.in0.re";

    public static GitHubConfig Load(string directory)
    {
        var path = Path.Combine(directory, "GitHubConfig.json");
        if (!File.Exists(path)) return new GitHubConfig();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<GitHubConfig>(json) ?? new GitHubConfig();
    }

    public void Save(string directory)
    {
        var path = Path.Combine(directory, "GitHubConfig.json");
        Directory.CreateDirectory(directory);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
