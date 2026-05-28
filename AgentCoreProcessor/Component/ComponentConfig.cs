// AgentCoreProcessor/Component/ComponentConfig.cs
using System.Text.Json;
using AgentCoreProcessor.Config;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

internal class ComponentConfig
{
    private static string ConfigPath =>
        Path.Combine(PathConfig.StoragePath, "Engine", "ComponentConfig.json");

    public int ShutdownTimeoutMs { get; set; } = 30000;
    public Dictionary<string, ComponentPermEntry> Components { get; set; } = new();

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

    /// <summary>检查组件是否对指定引擎类型启用。</summary>
    public bool IsEnabled(string componentName, string engineType, bool defaultEnabled = true)
    {
        if (Components.TryGetValue(componentName, out var entry))
            return entry.IsEnabled(engineType);
        return defaultEnabled;
    }

    /// <summary>检查组件是否对指定引擎类型启用，尊重 LoopApplicability 声明。</summary>
    public bool IsEnabled(string componentName, string engineType, bool defaultEnabled, Applicability applicability)
    {
        if (Components.TryGetValue(componentName, out var entry))
            return entry.IsEnabled(engineType);
        if (applicability == Applicability.NotApplicable)
            return false;
        return defaultEnabled;
    }

    /// <summary>修改组件在指定引擎类型下的启用状态并持久化。</summary>
    public void SetEnabled(string componentName, string engineType, bool enabled)
    {
        if (!Components.TryGetValue(componentName, out var entry))
        {
            entry = new ComponentPermEntry();
            Components[componentName] = entry;
        }
        entry.SetEnabled(engineType, enabled);
        Save();
    }

    /// <summary>获取所有已知引擎类型。</summary>
    public static readonly string[] EngineTypes = ["channel", "system", "subAgent", "review"];

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            _cached = this;
        }
        catch
        {
        }
    }
}

internal class ComponentPermEntry
{
    public bool Channel { get; set; } = true;
    public bool System { get; set; } = true;
    public bool SubAgent { get; set; } = true;
    public bool Review { get; set; } = true;

    public bool IsEnabled(string engineType) => engineType switch
    {
        "channel" => Channel,
        "system" => System,
        "sub-agent" => SubAgent,
        "review" => Review,
        _ => true
    };

    public void SetEnabled(string engineType, bool enabled)
    {
        switch (engineType)
        {
            case "channel": Channel = enabled; break;
            case "system": System = enabled; break;
            case "sub-agent": SubAgent = enabled; break;
            case "review": Review = enabled; break;
        }
    }
}
