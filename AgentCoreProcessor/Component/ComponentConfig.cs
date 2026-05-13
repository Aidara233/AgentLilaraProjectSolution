// AgentCoreProcessor/Component/ComponentConfig.cs
using System.Text.Json;
using AgentCoreProcessor.Config;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

internal class ComponentConfigEntry
{
    public bool? Enabled { get; set; }
    public Visibility? ToolVisibility { get; set; }
}

internal class ComponentConfig
{
    private static string ConfigPath =>
        Path.Combine(PathConfig.StoragePath, "Engine", "ComponentConfig.json");

    public int ShutdownTimeoutMs { get; set; } = 30000;
    public Dictionary<string, ComponentConfigEntry> Components { get; set; } = new();

    private static ComponentConfig? _cached;

    public static ComponentConfig Load()
    {
        if (_cached != null) return _cached;

        if (!File.Exists(ConfigPath))
        {
            _cached = new ComponentConfig();
            return _cached;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            _cached = JsonSerializer.Deserialize<ComponentConfig>(json) ?? new();
        }
        catch
        {
            _cached = new ComponentConfig();
        }
        return _cached;
    }

    public static void Invalidate() => _cached = null;

    public bool IsEnabled(string componentName, bool defaultEnabled)
    {
        if (Components.TryGetValue(componentName, out var entry) && entry.Enabled.HasValue)
            return entry.Enabled.Value;
        return defaultEnabled;
    }

    public Visibility GetVisibility(string componentName, Visibility defaultVisibility)
    {
        if (Components.TryGetValue(componentName, out var entry) && entry.ToolVisibility.HasValue)
            return entry.ToolVisibility.Value;
        return defaultVisibility;
    }
}
