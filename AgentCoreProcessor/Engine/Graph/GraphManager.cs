using System.Text.Json;
using System.Text.Json.Serialization;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;

namespace AgentCoreProcessor.Engine.Graph;

/// <summary>一条挂载记录：哪个作用域用哪张图。</summary>
internal sealed class GraphMount
{
    /// <summary>作用域：global / channel / session。</summary>
    public string Scope { get; set; } = "channel";

    /// <summary>作用域键：channel 是内部频道 id；session 是 RuntimeSession.SessionId；global 为空。</summary>
    public string? ScopeKey { get; set; }

    /// <summary>图类 id，对应 Storage/Graphs/{classId}.json。</summary>
    public string ClassId { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>实例键：挂载点唯一标识。classId 可被多个挂载点共用，实例绝不共用。</summary>
    public string InstanceKey => $"{ClassId}@{Scope}#{ScopeKey ?? string.Empty}";
}

internal sealed class MountDocument
{
    public int Version { get; set; } = 1;
    public List<GraphMount> Mounts { get; set; } = new();
}

/// <summary>
/// 挂载表 + 实例池：按频道选图，**每个挂载点一个实例**。
/// <para>⚠️ <b>没有挂载就等于不干预</b>——<see cref="For"/> 返回 null，调用方走原有行为。</para>
/// <para>⚠️ 实例生命周期跟随挂载记录：挂载表变动（文件时间戳变化）时重建，
/// 被移除的挂载点连同变量一起销毁；频道释放可调 <see cref="Release"/> 单独销毁。</para>
/// <para>⚠️ 图类（GraphClass）是模板，按 classId 缓存；实例（GraphInstance）按挂载点隔离。</para>
/// </summary>
internal sealed class GraphManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly bool _active;
    private readonly string _storagePath;
    private readonly string _mountsPath;
    private DateTime _mountsStamp;

    /// <summary>图模板：classId → 已加载的 GraphClass。</summary>
    private readonly Dictionary<string, GraphClass> _classes = new(StringComparer.Ordinal);

    /// <summary>运行实例：挂载点键 → 运行器（内含 GraphInstance 与变量）。</summary>
    private readonly Dictionary<string, KeywordGraphRunner> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionInstanceKeys = new(StringComparer.Ordinal);

    private readonly Dictionary<string, GraphMount> _byChannel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphMount> _bySession = new(StringComparer.Ordinal);
    private GraphMount? _global;

    private GraphManager(bool active, string storagePath)
    {
        _active = active;
        _storagePath = storagePath;
        _mountsPath = Path.Combine(storagePath, "Graphs", "mounts.json");
    }

    public bool IsActive => _active;

    /// <summary>当前存活实例数（诊断用）。</summary>
    public int InstanceCount => _instances.Count;

    public static bool ActiveFromEnvironment() =>
        string.Equals(
            Environment.GetEnvironmentVariable("LILARA_GRAPH_MODE"),
            "active",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>没有 mounts.json / 没有启用的挂载 → 返回 null（不干预）。</summary>
    public static GraphManager? TryCreate(string storagePath, bool active)
    {
        try
        {
            var manager = new GraphManager(active, storagePath);
            if (!manager.ParseMounts())
            {
                return null;
            }

            return manager._classes.Count == 0 ? null : manager;
        }
        catch (Exception ex)
        {
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.load_failed", new { error = ex.Message });
            return null;
        }
    }

    /// <summary>该频道应使用的图运行器（每挂载点一个实例）；未挂载返回 null。</summary>
    public KeywordGraphRunner? For(int channelId)
    {
        EnsureFresh();

        var mount = ResolveChannelMount(channelId);
        return mount is null ? null : GetOrCreate(mount);
    }

    /// <summary>只取精确匹配 sessionId 的 session-scope 挂载；未显式挂载返回 null，不回退共享作用域。</summary>
    public KeywordGraphRunner? ForSession(string sessionId)
    {
        EnsureFresh();
        if (!_bySession.TryGetValue(sessionId, out var mount) || !_classes.ContainsKey(mount.ClassId))
        {
            return null;
        }

        return GetSessionInstance(sessionId, mount);
    }

    private KeywordGraphRunner GetSessionInstance(string sessionId, GraphMount mount)
    {
        if (_sessionInstanceKeys.TryGetValue(sessionId, out var existingKey)
            && _instances.TryGetValue(existingKey, out var existing))
        {
            return existing;
        }

        var instanceKey = mount.InstanceKey;
        var graph = new GraphInstance(_classes[mount.ClassId], instanceKey);
        var runner = KeywordGraphRunner.ForInstance(graph, _active);
        _instances[instanceKey] = runner;
        _sessionInstanceKeys[sessionId] = instanceKey;

        Logging.Signal.Event(Logging.LogGroup.Engine, "graph.session.instance_created",
            new { sessionId, mount.ClassId, instanceKey });
        return runner;
    }

    /// <summary>频道作用域实例销毁（频道生命周期钩子）。</summary>
    public void Release(int channelId)
    {
        if (_byChannel.TryGetValue(channelId.ToString(), out var mount))
        {
            ReleaseMount(mount, new { channelId });
        }
    }

    /// <summary>RuntimeSession 完成或被 kill 时销毁它自己的实例，不论来源是 session/channel/global 挂载。</summary>
    public void ReleaseSession(string sessionId)
    {
        if (!_sessionInstanceKeys.Remove(sessionId, out var instanceKey))
        {
            return;
        }

        if (_instances.Remove(instanceKey))
        {
            Logging.Signal.Event(Logging.LogGroup.Engine, "graph.session.instance_released",
                new { sessionId, key = instanceKey });
        }
    }

    private void ReleaseMount(GraphMount mount, object detail)
    {
        if (_instances.Remove(mount.InstanceKey))
        {
            Logging.Signal.Event(Logging.LogGroup.Engine, "graph.mount.released",
                new { mount.ClassId, key = mount.InstanceKey, detail });
        }
    }

    /// <summary>挂载表变动 → 重建挂载并丢弃所有实例（变量随挂载记录一起失效）。</summary>
    private void EnsureFresh()
    {
        try
        {
            if (!File.Exists(_mountsPath))
            {
                return;
            }

            var stamp = File.GetLastWriteTimeUtc(_mountsPath);
            if (stamp == _mountsStamp)
            {
                return;
            }

            _mountsStamp = stamp;

            var dropped = _instances.Count;
            _instances.Clear();
            _classes.Clear();
            _byChannel.Clear();
            _bySession.Clear();
            _global = null;

            ParseMounts();

            Logging.Signal.Event(Logging.LogGroup.Engine, "graph.mount.reloaded", new
            {
                droppedInstances = dropped,
                classes = _classes.Count,
                channelMounts = _byChannel.Count,
                hasGlobal = _global is not null,
            });
        }
        catch (Exception ex)
        {
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.reload_failed", new { error = ex.Message });
        }
    }

    /// <summary>读取 mounts.json 并解析挂载。返回 false 表示没有可用挂载表。</summary>
    private bool ParseMounts()
    {
        if (!File.Exists(_mountsPath))
        {
            return false;
        }

        _mountsStamp = File.GetLastWriteTimeUtc(_mountsPath);

        var doc = JsonSerializer.Deserialize<MountDocument>(File.ReadAllText(_mountsPath), JsonOptions);
        if (doc?.Mounts is null || doc.Mounts.Count == 0)
        {
            return false;
        }

        var dir = Path.Combine(_storagePath, "Graphs");

        foreach (var mount in doc.Mounts.Where(m => m.Enabled))
        {
            if (string.IsNullOrWhiteSpace(mount.ClassId))
            {
                continue;
            }

            var scope = mount.Scope?.Trim().ToLowerInvariant();
            if (scope is not ("global" or "channel" or "session"))
            {
                Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.invalid_scope",
                    new { mount.ClassId, mount.Scope });
                continue;
            }

            if ((scope == "channel" || scope == "session") && string.IsNullOrWhiteSpace(mount.ScopeKey))
            {
                Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.missing_scope_key",
                    new { mount.ClassId, mount.Scope });
                continue;
            }

            if (!_classes.ContainsKey(mount.ClassId))
            {
                var graph = LoadClass(dir, mount.ClassId);
                if (graph is null)
                {
                    Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.class_missing",
                        new { mount.ClassId, dir });
                    continue;
                }

                _classes[mount.ClassId] = graph;
            }

            if (string.Equals(mount.Scope, "global", StringComparison.OrdinalIgnoreCase))
            {
                _global = mount;
            }
            else if (string.Equals(mount.Scope, "session", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(mount.ScopeKey))
            {
                _bySession[mount.ScopeKey] = mount;
            }
            else if (string.Equals(mount.Scope, "channel", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(mount.ScopeKey))
            {
                _byChannel[mount.ScopeKey] = mount;
            }
        }

        Logging.Signal.Event(Logging.LogGroup.Engine, "graph.mount.loaded", new
        {
            classes = _classes.Count,
            channelMounts = _byChannel.Count,
            hasGlobal = _global is not null,
            active = _active,
        });

        return true;
    }

    private GraphMount? ResolveChannelMount(int channelId)
    {
        if (_byChannel.TryGetValue(channelId.ToString(), out var mount) && _classes.ContainsKey(mount.ClassId))
        {
            return mount;
        }

        return _global is not null && _classes.ContainsKey(_global.ClassId) ? _global : null;
    }

    private KeywordGraphRunner GetOrCreate(GraphMount mount)
    {
        if (_instances.TryGetValue(mount.InstanceKey, out var existing))
        {
            return existing;
        }

        var graph = new GraphInstance(_classes[mount.ClassId], mount.InstanceKey);
        var runner = KeywordGraphRunner.ForInstance(graph, _active);
        _instances[mount.InstanceKey] = runner;

        Logging.Signal.Event(Logging.LogGroup.Engine, "graph.mount.instance_created",
            new { key = mount.InstanceKey, instances = _instances.Count });

        return runner;
    }

    private static GraphClass? LoadClass(string mountDir, string classId)
    {
        // 优先 Storage/Graphs（数据，可编辑），回退到程序目录下的 Graphs（随构建发布的默认图）
        var candidates = new[]
        {
            Path.Combine(mountDir, $"{classId}.json"),
            Path.Combine(AppContext.BaseDirectory, "Graphs", $"{classId}.json"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return GraphLoader.Load(File.ReadAllText(path));
            }
        }

        return null;
    }
}
