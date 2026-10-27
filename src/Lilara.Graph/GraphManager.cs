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
    private readonly Dictionary<string, List<GraphMount>> _bySessionType = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _mountDeclarationOrder = new(StringComparer.Ordinal);
    private readonly List<GraphMount> _global = new();

    // ── 运行时订阅（与静态挂载分区：两个文件、两条重载路径、一个共享的类缓存）──

    private readonly string _subscriptionsPath;
    private DateTime _subscriptionsStamp;

    /// <summary>订阅记录：id → 记录。</summary>
    private readonly Dictionary<string, GraphSubscription> _subscriptions = new(StringComparer.Ordinal);

    /// <summary>
    /// 订阅的图实例。**故意与 <see cref="_instances"/> 分开**——静态挂载表一改就会清空
    /// 全部挂载实例，订阅不能跟着被误杀（模型建一条订阅不该让别的频道的计数器归零）。
    /// </summary>
    private readonly Dictionary<string, KeywordGraphRunner> _subscriptionRunners = new(StringComparer.Ordinal);

    /// <summary>信源 → 订阅列表（粗筛索引；细条件归图）。</summary>
    private readonly Dictionary<string, List<GraphSubscription>> _bySource = new(StringComparer.OrdinalIgnoreCase);

    private GraphManager(bool active, string storagePath)
    {
        _active = active;
        _storagePath = storagePath;
        _mountsPath = Path.Combine(storagePath, "Graphs", "mounts.json");
        _subscriptionsPath = Path.Combine(storagePath, "Graphs", "subscriptions.json");
    }

    public bool IsActive => _active;

    /// <summary>当前存活实例数（诊断用）。</summary>
    public int InstanceCount => _instances.Count;

    public static bool ActiveFromEnvironment() =>
        string.Equals(
            Environment.GetEnvironmentVariable("LILARA_GRAPH_MODE"),
            "active",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 打开图管理器：**总是返回实例**，即使一个挂载、一条订阅都没有。
    /// <para>需要它能被打开，是因为"运行时创建第一条订阅"这件事发生在配置为空的时候——
    /// 若此时拿到 null，模型永远无法建立第一条订阅。</para>
    /// <para>"没有配置就不干预"由 <see cref="RunnersFor"/> 返回空集合保证，而不是靠返回 null。</para>
    /// </summary>
    public static GraphManager Open(string storagePath, bool active)
    {
        var manager = new GraphManager(active, storagePath);
        manager.ParseMounts();
        manager.ParseSubscriptions();
        return manager;
    }

    /// <summary>只读场景用：完全没有配置（无挂载、无订阅）时返回 null，表示"这套机制没启用"。</summary>
    public static GraphManager? TryCreate(string storagePath, bool active)
    {
        try
        {
            var manager = Open(storagePath, active);

            // 只有运行时订阅、没有静态挂载表时也必须工作：订阅是独立机制，不该被"没配 mounts.json"挡住。
            if (manager._classes.Count == 0 && manager._subscriptions.Count == 0)
            {
                return null;
            }

            return manager;
        }
        catch (Exception ex)
        {
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.load_failed", new { error = ex.Message });
            return null;
        }
    }

    /// <summary>
    /// 该频道的消息图运行器（每挂载点一个实例，按 Order 排序）。
    /// 频道没有专属挂载时回退到 global 挂载；都没有则返回空集合（不干预）。
    /// </summary>
    public IReadOnlyList<KeywordGraphRunner> RunnersFor(int channelId) =>
        Runners(channelId, mount => !mount.IsErrorTrigger);

    /// <summary>
    /// 该频道的兜底图运行器：动作失败后由骨架以错误事件再激活一次。
    /// 与消息图同一套继承规则——频道级优先，否则用 global 兜底。
    /// </summary>
    public IReadOnlyList<KeywordGraphRunner> RunnersForError(int channelId) =>
        Runners(channelId, mount => mount.IsErrorTrigger);

    private IReadOnlyList<KeywordGraphRunner> Runners(int channelId, Func<GraphMount, bool> predicate)
    {
        EnsureFresh();

        var scoped = _byChannel.TryGetValue(channelId.ToString(), out var mounts)
            ? mounts.Where(predicate).ToList()
            : new List<GraphMount>();

        // 频道没有这一类挂载时回退 global（同类触发源）
        if (scoped.Count == 0)
        {
            scoped = _global.Where(predicate).ToList();
        }

        return scoped
            .Where(mount => _classes.ContainsKey(mount.ClassId))
            .Select(GetOrCreate)
            .ToList();
    }

    /// <summary>合并同类型模板挂载与本会话专属挂载；每个会话的变量和实例均隔离。</summary>
    public IReadOnlyList<KeywordGraphRunner> RunnersForSession(string sessionId, string? sessionType = null)
    {
        EnsureFresh();
        var mounts = new List<GraphMount>();
        if (sessionType is not null && _bySessionType.TryGetValue(sessionType, out var typed)) mounts.AddRange(typed);
        if (_bySession.TryGetValue(sessionId, out var own)) mounts.AddRange(own);

        return mounts
            .OrderBy(mount => mount.Order)
            .ThenBy(mount => _mountDeclarationOrder[mount.InstanceKey])
            .Where(mount => _classes.ContainsKey(mount.ClassId))
            .Select(mount => GetSessionInstance(sessionId, mount))
            .ToList();
    }

    private KeywordGraphRunner GetSessionInstance(string sessionId, GraphMount mount)
    {
        var key = mount.Scope == "session-type"
            ? $"{mount.InstanceKey}@session:{sessionId.Length}:{sessionId}"
            : mount.InstanceKey;
        if (_instances.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var graph = new GraphInstance(_classes[mount.ClassId], key);
        var parameters = new Dictionary<string, string>(mount.Params, StringComparer.OrdinalIgnoreCase)
        {
            ["sessionId"] = sessionId
        };
        var runner = KeywordGraphRunner.ForInstance(graph, _active, parameters);
        _instances[key] = runner;

        if (!_sessionInstanceKeys.TryGetValue(sessionId, out var keys))
        {
            keys = new List<string>();
            _sessionInstanceKeys[sessionId] = keys;
        }
        keys.Add(key);

        Logging.Signal.Event(Logging.LogGroup.Engine, "graph.session.instance_created",
            new { sessionId, mount.ClassId, instanceKey = key });
        return runner;
    }

    /// <summary>
    /// 频道作用域实例销毁（频道生命周期钩子）。global 挂载不随单个频道销毁。
    /// <para>该频道的**运行时订阅也一起销毁**：订阅绑频道，频道没了就没有宿主了。</para>
    /// </summary>
    public void Release(int channelId)
    {
        foreach (var subscription in _subscriptions.Values.Where(s => s.ChannelId == channelId).ToList())
        {
            RemoveSubscription(subscription.Id, reason: "channel-released");
        }

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

    // ── 运行时订阅（绑频道、按信源索引、带期限与次数）──

    /// <summary>当前活着的订阅数（诊断用）。</summary>
    public int SubscriptionCount => _subscriptions.Count;

    /// <summary>图本体目录。</summary>
    private string GraphDir => Path.Combine(_storagePath, "Graphs");

    /// <summary>
    /// 某个信源上的候选订阅。**粗筛只按信源**（一次字典查找），细条件交给图。
    /// <para>顺带做惰性清理：过期/次数用尽的订阅在这里被摘掉，不依赖定时器。</para>
    /// </summary>
    public IReadOnlyList<SubscriptionRunner> SubscriptionsForSource(string source)
    {
        EnsureSubscriptionsFresh();

        if (string.IsNullOrWhiteSpace(source) ||
            !_bySource.TryGetValue(source.Trim(), out var subscriptions) ||
            subscriptions.Count == 0)
        {
            return Array.Empty<SubscriptionRunner>();
        }

        var now = DateTimeOffset.UtcNow;
        var runners = new List<SubscriptionRunner>();

        foreach (var subscription in subscriptions.ToList())
        {
            if (subscription.IsExpired(now))
            {
                RemoveSubscription(subscription.Id, "expired");
                continue;
            }

            if (subscription.IsExhausted)
            {
                RemoveSubscription(subscription.Id, "exhausted");
                continue;
            }

            if (!subscription.Enabled)
            {
                continue;
            }

            var runner = GetOrCreateSubscriptionRunner(subscription);
            if (runner is not null)
            {
                runners.Add(new SubscriptionRunner(subscription, runner));
            }
        }

        return runners;
    }

    /// <summary>列出仍然有效的订阅（可按频道过滤）。</summary>
    public IReadOnlyList<GraphSubscription> ListSubscriptions(int? channelId = null)
    {
        EnsureSubscriptionsFresh();
        var now = DateTimeOffset.UtcNow;

        return _subscriptions.Values
            .Where(s => channelId is null || s.ChannelId == channelId)
            .Where(s => s.IsLive(now))
            .OrderBy(s => s.CreatedAt)
            .ToList();
    }

    /// <summary>新增一条订阅：校验 → 落盘 → 入索引。成功返回 null，失败返回原因。</summary>
    public string? AddSubscription(GraphSubscription subscription)
    {
        EnsureSubscriptionsFresh();

        if (subscription.ChannelId <= 0) return "invalid-channel";
        if (string.IsNullOrWhiteSpace(subscription.Source)) return "invalid-source";
        if (string.IsNullOrWhiteSpace(subscription.ClassId)) return "invalid-class";

        if (!_classes.ContainsKey(subscription.ClassId))
        {
            var graph = LoadClass(GraphDir, subscription.ClassId);
            if (graph is null)
            {
                Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.subscription.class_missing",
                    new { subscription.ClassId });
                return "class-missing";
            }

            _classes[subscription.ClassId] = graph;
        }

        if (string.IsNullOrWhiteSpace(subscription.Id))
        {
            subscription.Id = Guid.NewGuid().ToString("N")[..12];
        }

        subscription.Source = subscription.Source.Trim();
        subscription.CreatedAt = DateTimeOffset.UtcNow;
        subscription.FiredCount = 0;
        subscription.LastFiredAt = null;
        subscription.Enabled = true;

        _subscriptions[subscription.Id] = subscription;
        AddToSource(subscription);
        SaveSubscriptions();

        Logging.Signal.Event(Logging.LogGroup.Engine, "graph.subscription.added", new
        {
            subscription.Id,
            subscription.ChannelId,
            subscription.Source,
            subscription.ClassId,
            subscription.MaxFires,
            subscription.ExpiresAt,
        });

        return null;
    }

    /// <summary>移除一条订阅（同时销毁它的图实例）。</summary>
    public bool RemoveSubscription(string id, string reason = "removed")
    {
        if (!_subscriptions.Remove(id, out var subscription))
        {
            return false;
        }

        if (_bySource.TryGetValue(subscription.Source, out var list))
        {
            list.RemoveAll(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            if (list.Count == 0)
            {
                _bySource.Remove(subscription.Source);
            }
        }

        _subscriptionRunners.Remove(subscription.InstanceKey);
        SaveSubscriptions();

        Logging.Signal.Event(Logging.LogGroup.Engine, "graph.subscription.removed", new
        {
            id,
            reason,
            subscription.ChannelId,
            subscription.FiredCount,
        });

        return true;
    }

    /// <summary>
    /// 记一次触发。用尽次数后停用（不立即删，保留到下次清理，便于事后查"它触发过"）。
    /// </summary>
    public void RecordSubscriptionFired(string id, DateTimeOffset at)
    {
        if (!_subscriptions.TryGetValue(id, out var subscription))
        {
            return;
        }

        subscription.FiredCount++;
        subscription.LastFiredAt = at;
        if (subscription.IsExhausted)
        {
            subscription.Enabled = false;
        }

        SaveSubscriptions();
    }

    private void AddToSource(GraphSubscription subscription)
    {
        if (!_bySource.TryGetValue(subscription.Source, out var list))
        {
            list = new List<GraphSubscription>();
            _bySource[subscription.Source] = list;
        }

        list.Add(subscription);
    }

    private KeywordGraphRunner? GetOrCreateSubscriptionRunner(GraphSubscription subscription)
    {
        if (_subscriptionRunners.TryGetValue(subscription.InstanceKey, out var existing))
        {
            return existing;
        }

        if (!_classes.TryGetValue(subscription.ClassId, out var graphClass))
        {
            var loaded = LoadClass(GraphDir, subscription.ClassId);
            if (loaded is null)
            {
                Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.subscription.class_missing",
                    new { subscription.ClassId, subscription.Id });
                return null;
            }

            _classes[subscription.ClassId] = graphClass = loaded;
        }

        var instance = new GraphInstance(graphClass, subscription.InstanceKey);
        var runner = KeywordGraphRunner.ForInstance(instance, _active, subscription.AsMountParameters());
        _subscriptionRunners[subscription.InstanceKey] = runner;

        Logging.Signal.Event(Logging.LogGroup.Engine, "graph.subscription.instance_created",
            new { subscription.Id, subscription.ClassId, subscription.ChannelId });

        return runner;
    }

    /// <summary>订阅文件变动 → 重建索引。**不碰挂载实例**，反之亦然。</summary>
    private void EnsureSubscriptionsFresh()
    {
        try
        {
            if (!File.Exists(_subscriptionsPath))
            {
                return;
            }

            var stamp = File.GetLastWriteTimeUtc(_subscriptionsPath);
            if (stamp == _subscriptionsStamp)
            {
                return;
            }

            _subscriptionsStamp = stamp;
            ParseSubscriptions();

            Logging.Signal.Event(Logging.LogGroup.Engine, "graph.subscription.reloaded",
                new { subscriptions = _subscriptions.Count });
        }
        catch (Exception ex)
        {
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.subscription.reload_failed",
                new { error = ex.Message });
        }
    }

    private void ParseSubscriptions()
    {
        _subscriptions.Clear();
        _bySource.Clear();

        if (!File.Exists(_subscriptionsPath))
        {
            return;
        }

        _subscriptionsStamp = File.GetLastWriteTimeUtc(_subscriptionsPath);

        var doc = JsonSerializer.Deserialize<SubscriptionDocument>(
            File.ReadAllText(_subscriptionsPath), JsonOptions);

        if (doc?.Subscriptions is null || doc.Subscriptions.Count == 0)
        {
            return;
        }

        foreach (var subscription in doc.Subscriptions)
        {
            if (string.IsNullOrWhiteSpace(subscription.Id) ||
                string.IsNullOrWhiteSpace(subscription.Source) ||
                string.IsNullOrWhiteSpace(subscription.ClassId) ||
                subscription.ChannelId <= 0)
            {
                Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.subscription.invalid_record",
                    new { subscription.Id, subscription.ChannelId, subscription.Source, subscription.ClassId });
                continue;
            }

            subscription.Params ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _subscriptions[subscription.Id] = subscription;
            AddToSource(subscription);
        }
    }

    private void SaveSubscriptions()
    {
        try
        {
            Directory.CreateDirectory(GraphDir);

            var document = new SubscriptionDocument
            {
                Subscriptions = _subscriptions.Values.OrderBy(s => s.CreatedAt).ToList(),
            };

            var json = JsonSerializer.Serialize(document, JsonOptions);
            var temp = _subscriptionsPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _subscriptionsPath, overwrite: true);

            // 自己写的文件不该触发下一次重载
            _subscriptionsStamp = File.GetLastWriteTimeUtc(_subscriptionsPath);
        }
        catch (Exception ex)
        {
            Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.subscription.save_failed",
                new { error = ex.Message });
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
            _bySessionType.Clear();
            _mountDeclarationOrder.Clear();
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
            if (scope is not ("global" or "channel" or "session" or "session-type"))
            {
                Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.invalid_scope",
                    new { mount.ClassId, mount.Scope });
                continue;
            }

            if ((scope == "channel" || scope == "session" || scope == "session-type") && string.IsNullOrWhiteSpace(mount.ScopeKey))
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
            if (scope == "channel")
            {
                if (!int.TryParse(scopeKey, out var channelId) || channelId <= 0)
                {
                    Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.invalid_channel", new { mount.ClassId, scopeKey });
                    continue;
                }
                scopeKey = channelId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (scope == "session-type") scopeKey = scopeKey.ToLowerInvariant();
            mount.Scope = scope;
            mount.ScopeKey = scope == "global" ? null : scopeKey;

            // 触发源：message（收到消息）| error（动作失败后的兜底）
            var trigger = string.IsNullOrWhiteSpace(mount.Trigger)
                ? GraphMount.TriggerMessage
                : mount.Trigger.Trim().ToLowerInvariant();
            if (trigger is not (GraphMount.TriggerMessage or GraphMount.TriggerError))
            {
                Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.invalid_trigger",
                    new { mount.ClassId, mount.Trigger });
                continue;
            }
            mount.Trigger = trigger;

            var activation = _classes[mount.ClassId].Nodes.Single(n => n.IsActivationSource);
            if ((trigger == GraphMount.TriggerMessage && activation is not OnMessageEvent)
                || (trigger == GraphMount.TriggerError && activation is not OnErrorEvent))
            {
                Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.incompatible_trigger",
                    new { mount.ClassId, mount.Trigger });
                continue;
            }

            if (scope is "session" or "session-type")
            {
                try { SessionGraphContract.Validate(_classes[mount.ClassId]); }
                catch (ArgumentException ex)
                {
                    Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.session.invalid_contract",
                        new { mount.ClassId, error = ex.Message });
                    continue;
                }
            }

            // 同一作用域内实例键必须唯一（classId + 作用域 + 键 + 实例名）。
            if (!seenKeys.Add(mount.InstanceKey))
            {
                Logging.Signal.Warn(Logging.LogGroup.Engine, "graph.mount.duplicate_instance",
                    new { mount.ClassId, mount.Scope, mount.ScopeKey, mount.InstanceName });
                continue;
            }

            _mountDeclarationOrder[mount.InstanceKey] = _mountDeclarationOrder.Count;
            if (scope == "global")
            {
                _global.Add(mount);
            }
            else if (scope is "session" or "session-type")
            {
                AddTo(scope == "session-type" ? _bySessionType : _bySession, scopeKey, mount);
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
        foreach (var mounts in _bySessionType.Values) Sort(mounts);

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
