// AgentCoreProcessor/Component/ComponentConfig.cs
using System.Text.Json;
using AgentCoreProcessor.Config;

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
            // 大小写不敏感：兼容手写小写 key（channel/task/review）与 WebUI 序列化的 PascalCase（Channel/Task/Review）
            _cached = JsonSerializer.Deserialize<ComponentConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch
        {
            _cached = new ComponentConfig();
        }
        return _cached;
    }

    public static void Invalidate() => _cached = null;

    /// <summary>检查组件是否对指定权限组启用。未配置条目 = 不启用（config-required）。</summary>
    public bool IsEnabled(string componentName, string engineType, bool defaultEnabled = true)
    {
        if (Components.TryGetValue(componentName, out var entry))
            return entry.IsEnabled(engineType);
        return false;
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
    public bool Task { get; set; } = true;
    public bool Review { get; set; } = true;

    public bool IsEnabled(string engineType) => engineType switch
    {
        "channel" => Channel,
        "task" => Task,
        "review" => Review,
        _ => true
    };

    public void SetEnabled(string engineType, bool enabled)
    {
        switch (engineType)
        {
            case "channel": Channel = enabled; break;
            case "task": Task = enabled; break;
            case "review": Review = enabled; break;
        }
    }
}
