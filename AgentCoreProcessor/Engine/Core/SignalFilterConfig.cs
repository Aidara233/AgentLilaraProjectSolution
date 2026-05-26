using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Engine;

/// <summary>
/// 信号过滤器配置。控制哪些信号类型能唤醒引擎、哪些对模型可见。
/// </summary>
public class SignalFilterConfig
{
    /// <summary>能唤醒引擎的信号类型。</summary>
    public HashSet<SignalCategory> WakeFilter { get; set; } = new()
    {
        SignalCategory.ChannelMessage,
        SignalCategory.Delegation,
        SignalCategory.SystemEvent,
        SignalCategory.WatchSignal
    };

    /// <summary>对模型可见的信号类型。不可见的信号被 drain 但不注入上下文。</summary>
    public HashSet<SignalCategory> VisibilityFilter { get; set; } = new()
    {
        SignalCategory.ChannelMessage,
        SignalCategory.Delegation,
        SignalCategory.SystemEvent,
        SignalCategory.WatchSignal
    };

    public bool CanWake(SignalCategory category)
        => category == SignalCategory.Internal || WakeFilter.Contains(category);

    public bool IsVisible(SignalCategory category)
        => category == SignalCategory.Internal || VisibilityFilter.Contains(category);
}

/// <summary>
/// 全局信号过滤器配置管理。按引擎类型存储配置。
/// </summary>
public class SignalFilterManager
{
    private readonly string _configPath;
    private Dictionary<string, SignalFilterConfig> _configs = new();

    public SignalFilterManager(string configPath)
    {
        _configPath = configPath;
        Load();
    }

    public SignalFilterConfig GetConfig(string engineType)
    {
        if (_configs.TryGetValue(engineType, out var config))
            return config;
        return new SignalFilterConfig();
    }

    public void SetConfig(string engineType, SignalFilterConfig config)
    {
        _configs[engineType] = config;
        Save();
    }

    public Dictionary<string, SignalFilterConfig> GetAll() => new(_configs);

    private void Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                _configs = CreateDefaults();
                Save();
                return;
            }

            var json = File.ReadAllText(_configPath);
            var root = JObject.Parse(json);

            _configs = new Dictionary<string, SignalFilterConfig>();
            foreach (var (key, value) in root)
            {
                if (value is not JObject obj) continue;
                _configs[key] = ParseConfig(obj);
            }
        }
        catch
        {
            _configs = CreateDefaults();
        }
    }

    public void Save()
    {
        try
        {
            var root = new JObject();
            foreach (var (engineType, config) in _configs)
            {
                root[engineType] = new JObject
                {
                    ["wake"] = new JArray(config.WakeFilter.Select(c => c.ToString()).ToArray()),
                    ["visibility"] = new JArray(config.VisibilityFilter.Select(c => c.ToString()).ToArray())
                };
            }

            var dir = Path.GetDirectoryName(_configPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_configPath, root.ToString(Formatting.Indented));
        }
        catch (Exception ex) { Signal.Warn(LogGroup.Engine, "信号过滤配置保存失败", new { path = _configPath, error = ex.Message }); }
    }

    private static SignalFilterConfig ParseConfig(JObject obj)
    {
        var config = new SignalFilterConfig();

        if (obj["wake"] is JArray wakeArr)
        {
            config.WakeFilter = wakeArr
                .Select(t => Enum.TryParse<SignalCategory>(t.ToString(), out var c) ? c : (SignalCategory?)null)
                .Where(c => c.HasValue)
                .Select(c => c!.Value)
                .ToHashSet();
        }

        if (obj["visibility"] is JArray visArr)
        {
            config.VisibilityFilter = visArr
                .Select(t => Enum.TryParse<SignalCategory>(t.ToString(), out var c) ? c : (SignalCategory?)null)
                .Where(c => c.HasValue)
                .Select(c => c!.Value)
                .ToHashSet();
        }

        return config;
    }

    private static Dictionary<string, SignalFilterConfig> CreateDefaults()
    {
        return new Dictionary<string, SignalFilterConfig>
        {
            ["channel"] = new SignalFilterConfig(),
            ["system"] = new SignalFilterConfig
            {
                WakeFilter = new HashSet<SignalCategory>
                {
                    SignalCategory.Delegation,
                    SignalCategory.SystemEvent
                },
                VisibilityFilter = new HashSet<SignalCategory>
                {
                    SignalCategory.Delegation,
                    SignalCategory.SystemEvent,
                    SignalCategory.WatchSignal
                }
            }
        };
    }
}
