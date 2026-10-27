using System.Text.Json;
using AgentLilara.GraphRuntime.Serialization;

namespace AgentCoreProcessor.Tool.Host;

internal interface IWireAccess
{
    object Describe(string? classId = null);
    object Validate(string source);
    object Apply(string source);
    object Mount(string source);
}

/// <summary>图源码管理。修改先校验，旧版本与变更记录先落盘，再原子替换目标文件。</summary>
internal sealed class WireAccessImpl : IWireAccess
{
    private readonly string _directory;
    private readonly string _defaults;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public WireAccessImpl(string storagePath, string? defaults = null)
    {
        _directory = Path.Combine(storagePath, "Graphs");
        _defaults = defaults ?? Path.Combine(AppContext.BaseDirectory, "Graphs");
    }

    public object Describe(string? classId = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(classId))
            {
                var path = Resolve(classId);
                return new { classId, source = File.ReadAllText(path) };
            }
            var files = new[] { _directory, _defaults }
                .Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.json"))
                .Where(p => Path.GetFileName(p) is not ("mounts.json" or "subscriptions.json"))
                .Select(Path.GetFileNameWithoutExtension).Distinct().OrderBy(x => x).ToArray();
            return new
            {
                classes = files,
                nodeTypes = new BuiltInNodeFactory().NodeTypes,
                sample = NodeTypeCatalog.SampleSource(),
                mounts = ReadMounts()
            };
        }
    }

    public object Validate(string source)
    {
        var graph = GraphLoader.Load(source);
        CheckId(graph.ClassId);
        return new { valid = true, graph.ClassId, nodes = graph.Nodes.Count, links = graph.Links.Count };
    }

    public object Apply(string source)
    {
        // 使用同一个生产加载器校验；不会执行图或调用汇块。
        var graph = GraphLoader.Load(source);
        CheckId(graph.ClassId);
        lock (_gate)
        {
            var revision = Replace(Path.Combine(_directory, graph.ClassId + ".json"), source);
            return new { applied = true, graph.ClassId, revision,
                reload = "Existing instances keep their class until mounts are reloaded or the host restarts." };
        }
    }

    /// <summary>接收完整挂载表；空表可卸载全部图。写入前验证每条记录与引用图。</summary>
    public object Mount(string source)
    {
        lock (_gate)
        {
            var document = JsonSerializer.Deserialize<Engine.Graph.MountDocument>(source, Json)
                ?? throw new ArgumentException("mount document is empty");
            if (document.Version != 1 || document.Mounts is null)
                throw new ArgumentException("mount document requires version 1 and mounts array");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mount in document.Mounts)
            {
                if (mount is null) throw new ArgumentException("null mount");
                CheckId(mount.ClassId);
                mount.Scope = mount.Scope.Trim().ToLowerInvariant();
                mount.Trigger = mount.Trigger.Trim().ToLowerInvariant();
                if (mount.Scope is not ("channel" or "session" or "global"))
                    throw new ArgumentException("scope must be channel, session or global");
                if (mount.Scope == "global") mount.ScopeKey = null;
                else if (string.IsNullOrWhiteSpace(mount.ScopeKey))
                    throw new ArgumentException("scopeKey is required");
                else mount.ScopeKey = mount.ScopeKey.Trim();
                if (mount.Scope == "channel" && (!int.TryParse(mount.ScopeKey, out var channel) || channel <= 0))
                    throw new ArgumentException("channel scopeKey must be a positive channel id");
                if (mount.Trigger is not ("message" or "error"))
                    throw new ArgumentException("trigger must be message or error");
                var graph = GraphLoader.Load(File.ReadAllText(Resolve(mount.ClassId)));
                if (graph.ClassId != mount.ClassId) throw new ArgumentException("classId does not match source");
                var activation = graph.Nodes.Single(n => n.IsActivationSource);
                if ((mount.Trigger == "error" && activation is not AgentLilara.GraphRuntime.Graph.OnErrorEvent)
                    || (mount.Trigger == "message" && activation is not AgentLilara.GraphRuntime.Graph.OnMessageEvent))
                    throw new ArgumentException("mount trigger does not match graph activation source");
                if (!keys.Add(mount.InstanceKey)) throw new ArgumentException("duplicate mount instance");
            }
            var revision = Replace(Path.Combine(_directory, "mounts.json"), JsonSerializer.Serialize(document, Json));
            return new { mounted = document.Mounts.Count, revision };
        }
    }

    private object ReadMounts() => File.Exists(Path.Combine(_directory, "mounts.json"))
        ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(_directory, "mounts.json")))
        : new { version = 1, mounts = Array.Empty<object>() };

    private string Resolve(string classId)
    {
        CheckId(classId);
        var path = Path.Combine(_directory, classId + ".json");
        if (!File.Exists(path)) path = Path.Combine(_defaults, classId + ".json");
        if (!File.Exists(path)) throw new FileNotFoundException($"graph '{classId}' not found");
        return path;
    }

    private static void CheckId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id is "mounts" or "subscriptions"
            || !char.IsAsciiLetterOrDigit(id[0])
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException("classId must start with a letter/digit and contain only letters, digits, dots, hyphens or underscores; mounts/subscriptions are reserved");
    }

    private string Replace(string target, string source)
    {
        Directory.CreateDirectory(_directory);
        var revision = Guid.NewGuid().ToString("N");
        var history = Path.Combine(_directory, ".history", revision);
        Directory.CreateDirectory(history);
        var before = File.Exists(target) ? File.ReadAllText(target) : null;
        File.WriteAllText(Path.Combine(history, "change.json"), JsonSerializer.Serialize(new
        {
            revision, at = DateTimeOffset.UtcNow, file = Path.GetFileName(target), before, after = source
        }, Json));
        var temp = target + "." + revision + ".tmp";
        try
        {
            File.WriteAllText(temp, source);
            File.Move(temp, target, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return revision;
    }
}
