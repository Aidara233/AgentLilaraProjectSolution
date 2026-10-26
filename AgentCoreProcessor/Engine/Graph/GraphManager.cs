using System.Text.Json;
using System.Text.Json.Serialization;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;

namespace AgentCoreProcessor.Engine.Graph;

internal sealed class MountDocument
{
    public int Version { get; set; } = 1;
    public List<GraphMount> Mounts { get; set; } = new();
}

/// <summary>
/// 挂载表 + 实例池：**每个挂载点一个实例**，同一作用域可挂多张图。
/// <para>⚠️ <b>没有挂载就等于不干预</b>——<see cref="RunnersFor"/> 返回空集合，调用方走原有行为。</para>
/// <para>⚠️ 实例生命周期跟随挂载记录：挂载表变动（文件时间戳变化）时重建，
/// 被移除的挂载点连同变量一起销毁；频道/会话释放可调 <see cref="Release"/> / <see cref="ReleaseSession"/>。</para>
/// <para>⚠️ 图类（GraphClass）是模板，按 classId 缓存；实例（GraphInstance）按挂载点隔离。</para>
/// <para>⚠️ 挂载顺序（<see cref="GraphMount.Order"/>）只决定跑图次序，不构成返回值依赖：
/// 每张图各自求解、各自待命，互不读取彼此结论。</para>
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

    /// <summary>会话 → 它的全部实例键（一个会话可挂多张图）。</summary>
    private readonly Dictionary<string, List<string>> _sessionInstanceKeys = new(StringComparer.Ordinal);

    /// <summary>作用域键 → 挂载列表（已按 Order 排序）。同一频道可挂多张图。</summary>
    private readonly Dictionary<string, List<GraphMount>> _byChannel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GraphMount>> _bySession = new(StringComparer.Ordinal);
    private readonly List<GraphMount> _global = new();

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

    /// <summary>
    /// 该频道的全部图运行器（每挂载点一个实例，按 Order 排序）。
    /// 频道没有专属挂载时回退到 global 挂载；都没有则返回空集合（不干预）。
    /// </summary>
    public IReadOnlyList<KeywordGraphRunner> RunnersFor(int channelId)
    {
        EnsureFresh();

        return ResolveChannelMounts(channelId)
            .Where(mount => _classes.ContainsKey(mount.ClassId))
            .Select(GetOrCreate)
            .ToList();
    }

    /// <summary>只取精确匹配 sessionId 的 session-scope 挂载；未显式挂载返回空集合，不回退共享作用域。</summary>
    public IReadOnlyList<KeywordGraphRunner> RunnersForSession(string sessionId)
    {
        EnsureFresh();
        if (!_bySession.TryGetValue(sessionId, out var mounts) || mounts.Count == 0)
        {
            return Array.Empty<KeywordGraphRunner>();
        }

        return mounts
            .Where(mount => _classes.ContainsKey(mount.ClassId))
            .Select(mount => GetSessionInstance(sessionId, mount))
            .ToList();
    }

    private KeywordGraphRunner GetSessionInstance(string sessionId, GraphMount mount)
    {
        if (_instances.TryGetValue(mount.InstanceKey, out var existing))
        {
            return existing;
        }

        var graph = new GraphInstance(_classes[mount.ClassId], mount.InstanceKey);
        var runner = KeywordGraphRunner.ForInstance(graph, _active, mount.Params);
        _instances[mount.InstanceKey] = runner;

        if (!_sessionInstanceKeys.TryGetValue(sessionId, out var keys))
        {
            keys = new List<string>();
            _sessionInstanceKeys[sessionId] = keys;
        }
        keys.Add(mount.InstanceKey);

        Logging.Signal.Event(Logging.LogGroup.Engine, "graph.session.instance_created",
            new { sessionId, mount.ClassId, instanceKey = mount.InstanceKey });
        return runner;
    }

    /// <summary>频道作用域实例销毁（频道生命周期钩子）。global 挂载不随单个频道销毁。</summary>
    public void Release(int channelId)
    {
        if (!_byChannel.TryGetValue(channelId.ToString(), out var mounts))
        {
            return;
        }

        foreach (var mount in mounts)
        {
            ReleaseMount(mount, new { channelId });
        }
    }

    /// <summary>RuntimeSession 完成或被 kill 时销毁它自己的全部实例。</summary>
    public void ReleaseSession(string sessionId)
    {
        if (!_sessionInstanceKeys.Remove(sessionId, out var keys))
        {
            return;
        }

        foreach (var key in keys)
        {
            if (_instances.Remove(key))
            {
                Logging.Signal.Event(Logging.LogGroup.Engine, "graph.session.instance_released",
                    new { sessionId, key });
            }
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
            _sessionInstanceKeys.Clear();
            _global.Clear();

            ParseMounts();

            Logging.Signal.Event(Logging.LogGroup.Engine, "graph.mount.reloaded", new
            {
                droppedInstances = dropped,
                classes = _classes.Count,
                channelMounts = _byChannel.Values.Sum(mounts => mounts.Count),
                globalMounts = _global.Count,
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
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        // 声明顺序作为 Order 相同时的次序（OrderBy 是稳定排序）
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

            var scopeKey = scope == "global" ? string.Empty : mount.ScopeKey!.Trim();
            mount.Scope = scope;
            mount.ScopeKey = scope == "global" ? null : scopeKey;

            // 同一作用域内实例键必须唯一（classId + 作用域 + 键 + 实例名）。
            if (!seenKeys.Add(mount.InstanceKey))
            {
                Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.duplicate_instance",
                    new { mount.ClassId, mount.Scope, mount.ScopeKey, mount.InstanceName });
                continue;
            }

            if (scope == "global")
            {
                _global.Add(mount);
            }
            else if (scope == "session")
            {
                AddTo(_bySession, scopeKey, mount);
            }
            else
            {
                AddTo(_byChannel, scopeKey, mount);
            }
        }

        // 按 Order 排序（同一 Order 保持声明顺序），保证跑图次序可复现
        Sort(_global);
        foreach (var mounts in _byChannel.Values) Sort(mounts);
        foreach (var mounts in _bySession.Values) Sort(mounts);

        Logging.Signal.Event(Logging.LogGroup.Engine, "graph.mount.loaded", new
        {
            classes = _classes.Count,
            channelMounts = _byChannel.Values.Sum(mounts => mounts.Count),
            sessionMounts = _bySession.Values.Sum(mounts => mounts.Count),
            globalMounts = _global.Count,
            active = _active,
        });

        return true;
    }

    private static void AddTo(Dictionary<string, List<GraphMount>> index, string key, GraphMount mount)
    {
        if (!index.TryGetValue(key, out var mounts))
        {
            mounts = new List<GraphMount>();
            index[key] = mounts;
        }
        mounts.Add(mount);
    }

    private static void Sort(List<GraphMount> mounts)
    {
        var ordered = mounts.OrderBy(mount => mount.Order).ToList();
        mounts.Clear();
        mounts.AddRange(ordered);
    }

    /// <summary>频道专属挂载优先；没有则用 global 挂载。</summary>
    private IReadOnlyList<GraphMount> ResolveChannelMounts(int channelId)
    {
        if (_byChannel.TryGetValue(channelId.ToString(), out var mounts) && mounts.Count > 0)
        {
            return mounts;
        }

        return _global;
    }

    private KeywordGraphRunner GetOrCreate(GraphMount mount)
    {
        if (_instances.TryGetValue(mount.InstanceKey, out var existing))
        {
            return existing;
        }

        var graph = new GraphInstance(_classes[mount.ClassId], mount.InstanceKey);
        var runner = KeywordGraphRunner.ForInstance(graph, _active, mount.Params);
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
