using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Plugin.GitTools.Config;

public class RepoEntry
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string RemoteUrl { get; set; } = "";
    public string? GitHubOwner { get; set; }
    public string? GitHubRepo { get; set; }
    public DateTime RegisteredAt { get; set; } = DateTime.Now;
}

public class RepoRegistry
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public RepoRegistry(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "repos.json");
    }

    public Dictionary<string, RepoEntry> Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath)) return new Dictionary<string, RepoEntry>();
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<Dictionary<string, RepoEntry>>(json)
                       ?? new Dictionary<string, RepoEntry>();
            }
            catch { return new Dictionary<string, RepoEntry>(); }
        }
    }

    public void Save(Dictionary<string, RepoEntry> repos)
    {
        lock (_lock)
        {
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(repos, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _filePath, overwrite: true);
        }
    }

    public void Register(RepoEntry entry)
    {
        lock (_lock)
        {
            var repos = LoadInternal();
            repos[entry.RelativePath] = entry;
            SaveInternal(repos);
        }
    }

    public RepoEntry? GetByName(string name)
    {
        lock (_lock)
        {
            var repos = LoadInternal();
            return repos.Values.FirstOrDefault(r =>
                string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public RepoEntry? GetByPath(string relativePath)
    {
        lock (_lock)
        {
            var repos = LoadInternal();
            return repos.GetValueOrDefault(relativePath);
        }
    }

    public List<RepoEntry> ListAll()
    {
        lock (_lock)
        {
            return LoadInternal().Values.ToList();
        }
    }

    private Dictionary<string, RepoEntry> LoadInternal()
    {
        try
        {
            if (!File.Exists(_filePath)) return new Dictionary<string, RepoEntry>();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, RepoEntry>>(json)
                   ?? new Dictionary<string, RepoEntry>();
        }
        catch { return new Dictionary<string, RepoEntry>(); }
    }

    private void SaveInternal(Dictionary<string, RepoEntry> repos)
    {
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(repos, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _filePath, overwrite: true);
    }
}
