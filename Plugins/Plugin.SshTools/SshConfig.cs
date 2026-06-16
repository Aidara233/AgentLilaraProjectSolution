// Plugins/Plugin.SshTools/SshConfig.cs
using System.Text.Json;

namespace Plugin.SshTools;

public class SshConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string KeyPath { get; set; } = "";
    public int MaxOutputChars { get; set; } = 4000;
    public int MaxTimeoutSeconds { get; set; } = 120;
    public int IdleTimeoutSeconds { get; set; } = 300;
    public int ReconnectDelaySeconds { get; set; } = 5;

    public static SshConfig Load(string configDir)
    {
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "SshTools.json");

        if (!File.Exists(path))
        {
            var defaults = new SshConfig();
            var defaultJson = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, defaultJson);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<SshConfig>(json, options) ?? new SshConfig();
        }
        catch
        {
            return new SshConfig();
        }
    }

    public string ResolveKeyPath(string configDir)
    {
        if (string.IsNullOrEmpty(KeyPath)) return "";
        if (Path.IsPathRooted(KeyPath)) return KeyPath;
        return Path.GetFullPath(Path.Combine(configDir, KeyPath));
    }

}
