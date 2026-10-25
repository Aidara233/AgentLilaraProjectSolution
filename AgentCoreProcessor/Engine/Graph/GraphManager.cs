using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCoreProcessor.Config;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;

namespace AgentCoreProcessor.Engine.Graph;

/// <summary>一条挂载记录：哪个作用域用哪张图。</summary>
internal sealed class GraphMount
{
    /// <summary>作用域：目前支持 channel / global。</summary>
    public string Scope { get; set; } = "channel";

    /// <summary>作用域键：channel 作用域下是内部频道 id。</summary>
    public string? ScopeKey { get; set; }

    /// <summary>图类 id，对应 Storage/Graphs/{classId}.json。</summary>
    public string ClassId { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

internal sealed class MountDocument
{
    public int Version { get; set; } = 1;
    public List<GraphMount> Mounts { get; set; } = new();
}

/// <summary>
/// 挂载表：按频道决定用哪张图。
/// <para>⚠️ <b>没有挂载就等于不干预</b>——<see cref="For"/> 返回 null，调用方走原有行为。</para>
/// <para>⚠️ 加载失败、图文件缺失都只告警并跳过该挂载，不影响消息链。</para>
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
    private readonly Dictionary<string, KeywordGraphRunner> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphMount> _byChannel = new(StringComparer.Ordinal);
    private GraphMount? _global;

    private GraphManager(bool active)
    {
        _active = active;
    }

    /// <summary>active 表示图的结论会真正影响行为；否则只记录。</summary>
    public bool IsActive => _active;

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
            var dir = Path.Combine(storagePath, "Graphs");
            var mountsPath = Path.Combine(dir, "mounts.json");
            if (!File.Exists(mountsPath))
            {
                return null;
            }

            var doc = JsonSerializer.Deserialize<MountDocument>(File.ReadAllText(mountsPath), JsonOptions);
            if (doc?.Mounts is null || doc.Mounts.Count == 0)
            {
                return null;
            }

            var manager = new GraphManager(active);

            foreach (var mount in doc.Mounts.Where(m => m.Enabled))
            {
                if (string.IsNullOrWhiteSpace(mount.ClassId))
                {
                    continue;
                }

                if (!manager._classes.ContainsKey(mount.ClassId))
                {
                    var runner = LoadRunner(dir, mount.ClassId, active);
                    if (runner is null)
                    {
                        Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.class_missing",
                            new { mount.ClassId, dir });
                        continue;
                    }

                    manager._classes[mount.ClassId] = runner;
                }

                if (string.Equals(mount.Scope, "global", StringComparison.OrdinalIgnoreCase))
                {
                    manager._global = mount;
                }
                else if (!string.IsNullOrWhiteSpace(mount.ScopeKey))
                {
                    manager._byChannel[mount.ScopeKey] = mount;
                }
            }

            manager._activeCount = manager._classes.Count;
            Logging.Signal.Event(Logging.LogGroup.Engine, "graph.mount.loaded", new
            {
                classes = manager._classes.Count,
                channelMounts = manager._byChannel.Count,
                hasGlobal = manager._global is not null,
                active,
            });

            return manager._classes.Count == 0 ? null : manager;
        }
        catch (Exception ex)
        {
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.load_failed", new { error = ex.Message });
            return null;
        }
    }

    private int _activeCount;

    /// <summary>该频道应使用的图运行器；未挂载返回 null。</summary>
    public KeywordGraphRunner? For(int channelId)
    {
        if (_byChannel.TryGetValue(channelId.ToString(), out var mount)
            && _classes.TryGetValue(mount.ClassId, out var runner))
        {
            return runner;
        }

        if (_global is not null && _classes.TryGetValue(_global.ClassId, out var globalRunner))
        {
            return globalRunner;
        }

        return null;
    }

    private static KeywordGraphRunner? LoadRunner(string mountDir, string classId, bool active)
    {
        // 优先 Storage/Graphs（数据，可编辑），回退到程序目录下的 Graphs（随构建发布的默认图）
        var candidates = new[]
        {
            Path.Combine(mountDir, $"{classId}.json"),
            Path.Combine(AppContext.BaseDirectory, "Graphs", $"{classId}.json"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var graph = new GraphInstance(GraphLoader.Load(File.ReadAllText(path)));
            return KeywordGraphRunner.ForInstance(graph, active);
        }

        return null;
    }
}
