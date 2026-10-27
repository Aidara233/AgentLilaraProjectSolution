using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.WebUI;
using Newtonsoft.Json;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class SessionsProvider : IWebUIProvider
{
    public string Id => "core-sessions";
    public string DisplayName => "会话";

    private readonly MasterEngine _engine;

    public SessionsProvider(MasterEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<PageDefinition> Pages => new List<PageDefinition>
    {
        BuildActiveSessionsPage(),
        BuildHistoryPage(),
        BuildInjectPage(),
        BuildSessionDetailPage()
    };

    // ---- 活跃会话 ----

    private PageDefinition BuildActiveSessionsPage() => new()
    {
        Route = "sessions",
        Meta = new PageMeta { Title = "活跃会话", Icon = "bi-cpu", Group = "会话引擎", Order = 35 },
        LayoutType = PageLayoutType.Sidebar,
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "sessions-active", Type = CardType.Table, DataSourceId = "sessions-active", Title = "活跃会话",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "sessionId", Header = "会话ID", Width = "140px" },
                        new() { Field = "status", Header = "状态", Width = "100px", Format = ColumnFormat.Badge },
                        new() { Field = "core", Header = "核心", Width = "110px" },
                        new() { Field = "components", Header = "组件" },
                        new() { Field = "parent", Header = "父会话", Width = "140px" },
                        new() { Field = "rounds", Header = "轮次", Width = "60px" }
                    },
                    RowActions = new()
                    {
                        new() { Id = "stop", Label = "停止", Confirm = "确认停止此会话？" },
                        new() { Id = "force-kill", Label = "强制终止", Danger = true, Confirm = "确认强制终止此会话？" }
                    },
                    Searchable = true,
                    Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 12 }
            },
            new()
            {
                Id = "core-switch", Type = CardType.Form, DataSourceId = "core-switch", Title = "切换核心",
                Schema = new FormSchema
                {
                    ShowSubmit = true,
                    Fields = new()
                    {
                        new() { Field = "sessionId", Label = "会话ID", Type = FormFieldType.Text, Required = true, Placeholder = "从上方列表复制会话ID" },
                        new() { Field = "coreName", Label = "目标核心", Type = FormFieldType.Select, Required = true, Options = CoreOptions() }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6, GridColumnStart = 1 },
                // 换核成功后活跃会话列表的“核心”列需要立即刷新。
                AffectedDataSourceIds = new[] { "sessions-active" }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "sessions-active", Source = new SessionsActiveSource(_engine), Paging = DataPagingMode.Client },
            new() { Id = "core-switch", Factory = () => new CoreSwitchSource(_engine) }
        }
    };

    // ---- 历史 ----

    private PageDefinition BuildHistoryPage() => new()
    {
        Route = "sessions/history",
        Meta = new PageMeta { Title = "历史", Icon = "bi-clock-history", Group = "会话引擎", Order = 36 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "sessions-history", Type = CardType.Table, DataSourceId = "sessions-history", Title = "会话历史",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "sessionId", Header = "会话ID", Width = "140px" },
                        new() { Field = "status", Header = "状态", Width = "100px", Format = ColumnFormat.Badge },
                        new() { Field = "components", Header = "组件" },
                        new() { Field = "rounds", Header = "轮次", Width = "70px" },
                        new() { Field = "createdAt", Header = "创建时间", Width = "160px" }
                    },
                    Searchable = true,
                    DefaultPageSize = 30
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "sessions-history", Source = new SessionsHistorySource(_engine), Paging = DataPagingMode.Server }
        }
    };

    // ---- 消息注入 ----

    private PageDefinition BuildInjectPage() => new()
    {
        Route = "sessions/inject",
        Meta = new PageMeta { Title = "消息注入", Icon = "bi-send", Group = "会话引擎", Order = 37 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "sessions-inject", Type = CardType.Form, DataSourceId = "sessions-inject", Title = "注入消息",
                Schema = new FormSchema
                {
                    ShowSubmit = true,
                    Fields = new()
                    {
                        new() { Field = "channelId", Label = "频道ID", Type = FormFieldType.Number, Required = true },
                        new() { Field = "content", Label = "消息内容", Type = FormFieldType.TextArea, Required = true },
                        new() { Field = "tag", Label = "标签", Type = FormFieldType.Select, DefaultValue = "normal", Options = new()
                        {
                            new() { Value = "normal", Label = "normal" },
                            new() { Value = "mention", Label = "mention" },
                            new() { Value = "command", Label = "command" }
                        }}
                    }
                },
                Layout = new CardLayout { PreferredCols = 6, GridColumnStart = 1 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "sessions-inject", Factory = () => new MessageInjectSource(_engine) }
        }
    };

    // ---- 会话详情（活跃与归档共用） ----

    private PageDefinition BuildSessionDetailPage() => new()
    {
        Route = "sessions/{sessionId}",
        Meta = new PageMeta { Title = "会话详情", Icon = "bi-cpu", Group = "会话引擎", Order = 38, ShowInNav = false },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "session-basic", Type = CardType.Status, DataSourceId = "session-detail", Title = "会话概览",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "sessionId", Label = "会话 ID" },
                        new() { Field = "type", Label = "类型", Type = StatusFieldType.Badge },
                        new() { Field = "status", Label = "状态", Type = StatusFieldType.Badge },
                        new() { Field = "alive", Label = "存活", Type = StatusFieldType.Indicator },
                        new() { Field = "core", Label = "当前核心" },
                        new() { Field = "created", Label = "创建时间" },
                        new() { Field = "rounds", Label = "轮次" },
                        new() { Field = "parent", Label = "父会话" },
                        new() { Field = "children", Label = "子会话" },
                        new() { Field = "channel", Label = "连接频道" },
                        new() { Field = "pool", Label = "资源池" },
                        new() { Field = "compression", Label = "压缩层级", Type = StatusFieldType.Badge },
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "session-components", Type = CardType.Status, DataSourceId = "session-components", Title = "组件",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "components", Label = "组件清单", IsMultiline = true },
                        new() { Field = "ports", Label = "端口", IsMultiline = true },
                        new() { Field = "note", Label = "说明", IsMultiline = true },
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "session-detail", Factory = () => new SessionDetailSource(_engine) },
            new() { Id = "session-components", Factory = () => new SessionComponentSource(_engine) }
        }
    };

    // ── 辅助：获取会话轮次 ──

    internal static int GetRoundCount(IRuntimeSession session) =>
        (session as IRuntimeSessionDiagnostics)?.RoundCount ?? 0;

    /// <summary>枚举 Storage/Core/*.json 的核心名，作为切换表单下拉选项（服务端按会话白名单再校验）。</summary>
    private static List<SelectOption> CoreOptions()
    {
        try
        {
            var dir = Path.Combine(PathConfig.StoragePath, "Core");
            if (!Directory.Exists(dir)) return new List<SelectOption>();
            return Directory.GetFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new SelectOption { Value = n!, Label = n! })
                .ToList();
        }
        catch { return new List<SelectOption>(); }
    }
}

// ======== 数据源 ========

/// <summary>
/// 活跃会话列表。
/// </summary>
internal class SessionsActiveSource : IDataSource
{
    private readonly MasterEngine _engine;
    public SessionsActiveSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var se = _engine.GetActiveEnginesSnapshot().OfType<SessionEngine>().FirstOrDefault();
        var sessions = se?.GetActiveSessions() ?? Array.Empty<IRuntimeSession>();

        var arr = new JsonArray();
        foreach (var s in sessions)
        {
            var components = s.ComponentIds.Count > 0
                ? string.Join(", ", s.ComponentIds)
                : "—";

            arr.Add(new JsonObject
            {
                ["sessionId"] = s.SessionId,
                ["status"] = s.Status.ToString(),
                ["type"] = (s is RuntimeSession rs ? rs.Type : "—"),
                ["core"] = (s is RuntimeSession rsCore ? rsCore.ActiveCoreConfig : "—"),
                ["children"] = s.ChildSessionIds.Count.ToString(),
                ["components"] = components,
                ["parent"] = s.ParentSessionId ?? "—",
                ["rounds"] = SessionsProvider.GetRoundCount(s).ToString(),
                ["_link"] = $"/p/sessions/{s.SessionId}"
            });
        }

        // 空态不伪造可操作会话行；前端会显示“本页暂无数据”。
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var sessionId = data?["sessionId"]?.ToString();
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult(new ActionResult { Success = false, Message = "缺少会话ID" });

        var se = _engine.GetActiveEnginesSnapshot().OfType<SessionEngine>().FirstOrDefault();
        if (se == null)
            return Task.FromResult(new ActionResult { Success = false, Message = "SessionEngine 不可用" });

        switch (action)
        {
            case "stop":
                var result = se.StopSession(sessionId, "WebUI 手动停止");
                return Task.FromResult(result.Success
                    ? new ActionResult { Success = true, Message = $"会话 {sessionId} 已停止" }
                    : new ActionResult
                    {
                        Success = false,
                        Message = result.BlockReason == "Has running children"
                            ? $"无法停止：存在活跃子会话 ({string.Join(", ", result.BlockingChildren ?? new())})"
                            : $"停止失败: {result.BlockReason ?? "未知原因"}"
                    });

            case "force-kill":
                se.ForceKill(sessionId);
                return Task.FromResult(new ActionResult { Success = true, Message = $"会话 {sessionId} 已强制终止" });

            default:
                return Task.FromResult(new ActionResult { Success = false, Message = $"未知操作: {action}" });
        }
    }
}

/// <summary>
/// 切换核心表单。sessionId + coreName → SessionEngine.SwitchSessionCore。
/// </summary>
internal class CoreSwitchSource : IDataSource
{
    private readonly MasterEngine _engine;
    public CoreSwitchSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;

    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var formDefaults = new JsonObject
        {
            ["sessionId"] = "",
            ["coreName"] = ""
        };
        return Task.FromResult(new DataResult { Data = formDefaults });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action != "save" || data is not JsonObject obj)
            return Task.FromResult(new ActionResult { Success = false, Message = "无效请求" });

        var sessionId = obj["sessionId"]?.ToString() ?? "";
        var coreName = obj["coreName"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(coreName))
            return Task.FromResult(new ActionResult { Success = false, Message = "会话ID与目标核心均必填" });

        var se = _engine.GetActiveEnginesSnapshot().OfType<SessionEngine>().FirstOrDefault();
        if (se == null)
            return Task.FromResult(new ActionResult { Success = false, Message = "SessionEngine 不可用" });

        var (ok, msg) = se.SwitchSessionCore(sessionId.Trim(), coreName.Trim());
        return Task.FromResult(new ActionResult { Success = ok, Message = msg });
    }
}

/// <summary>
/// 会话历史（扫描 Storage/Sessions/*/meta.json）。
/// </summary>
internal class SessionsHistorySource : IDataSource
{
    private readonly MasterEngine _engine;
    public SessionsHistorySource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;

    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var arr = new JsonArray();
        var sessionsDir = Path.Combine(PathConfig.StoragePath, "Sessions");

        if (!Directory.Exists(sessionsDir))
            return Task.FromResult(new DataResult { Data = arr, TotalCount = 0 });

        var dirs = Directory.GetDirectories(sessionsDir);
        var entries = new List<(string sessionId, string status, string components, string createdAt, int roundCount)>();

        foreach (var dir in dirs)
        {
            var sessionId = Path.GetFileName(dir);
            var statePath = Path.Combine(dir, "state.json");
            var specPath = Path.Combine(dir, "spec.json");
            var metaPath = Path.Combine(dir, "meta.json");

            try
            {
                if (File.Exists(statePath))
                {
                    // 现行存档：spec.json（不可变）+ state.json（每轮重写）+ history.jsonl
                    var state = JsonConvert.DeserializeObject<SessionStateProbe>(File.ReadAllText(statePath));
                    var spec = File.Exists(specPath)
                        ? JsonConvert.DeserializeObject<SessionSpecProbe>(File.ReadAllText(specPath))
                        : null;

                    var components = spec?.ResolvedComponentIds?.Length > 0
                        ? string.Join(", ", spec.ResolvedComponentIds)
                        : spec?.AdditionalComponents?.Length > 0
                            ? string.Join(", ", spec.AdditionalComponents)
                            : "—";
                    var created = state?.CreatedAt is DateTime dt && dt != default
                        ? Util.TimeUtil.ToLocalSafe(dt).ToString("yyyy-MM-dd HH:mm:ss")
                        : "—";

                    entries.Add((sessionId, state?.Status ?? "Unknown", components, created, state?.RoundCount ?? 0));
                }
                else if (File.Exists(metaPath))
                {
                    // 兼容旧 meta.json 存档（仅无 persister 的兜底路径会写）
                    var meta = JsonConvert.DeserializeAnonymousType(File.ReadAllText(metaPath), new
                    {
                        status = "",
                        componentIds = new List<string>(),
                        createdAt = DateTime.MinValue,
                        roundCount = 0
                    });
                    if (meta == null) continue;

                    var components = meta.componentIds?.Count > 0
                        ? string.Join(", ", meta.componentIds)
                        : "—";
                    var created = meta.createdAt != DateTime.MinValue
                        ? Util.TimeUtil.ToLocalSafe(meta.createdAt).ToString("yyyy-MM-dd HH:mm:ss")
                        : "—";

                    entries.Add((sessionId, meta.status, components, created, meta.roundCount));
                }
                else
                {
                    // spec 已写但 state 尚未落地 → 视为 Crashed（与 SessionPersistence.LoadState 一致）
                    entries.Add((sessionId, "Crashed", "—", "—", 0));
                }
            }
            catch { /* skip corrupt session archive */ }
        }

        // 按创建时间倒序（字符串时间戳可直接排序；"?—" 沉底）
        entries = entries.OrderByDescending(e => e.createdAt).ToList();

        // 搜索
        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var s = query.Search.ToLowerInvariant();
            entries = entries.Where(e =>
                e.sessionId.ToLowerInvariant().Contains(s) ||
                e.status.ToLowerInvariant().Contains(s) ||
                e.components.ToLowerInvariant().Contains(s)
            ).ToList();
        }

        var total = entries.Count;
        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 30;
        var paged = entries.Skip((page - 1) * pageSize).Take(pageSize);

        foreach (var entry in paged)
        {
            arr.Add(new JsonObject
            {
                ["sessionId"] = entry.sessionId,
                ["status"] = entry.status,
                ["components"] = entry.components,
                ["createdAt"] = entry.createdAt,
                ["rounds"] = entry.roundCount.ToString(),
                ["_link"] = $"/p/sessions/{entry.sessionId}"
            });
        }

        return Task.FromResult(new DataResult { Data = arr, TotalCount = total });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

/// <summary>
/// 会话详情：活跃会话直接从 RuntimeSession + 资源池读取；已归档会话回退到 persistence 三件套。
/// </summary>
internal class SessionDetailSource : IDataSource
{
    private readonly MasterEngine _engine;
    public SessionDetailSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var sessionId = query?.RouteParams?.GetValueOrDefault("sessionId") ?? "";
        if (string.IsNullOrEmpty(sessionId))
            return Task.FromResult(new DataResult { Data = new JsonObject { ["status"] = "未指定会话" } });

        var se = _engine.GetActiveEnginesSnapshot().OfType<SessionEngine>().FirstOrDefault();
        var session = se?.GetSession(sessionId) as RuntimeSession;

        if (session != null)
        {
            var pool = se!.GetPoolBySession(sessionId);
            var data = new JsonObject
            {
                ["sessionId"] = session.SessionId,
                ["type"] = session.Type,
                ["status"] = session.Status.ToString(),
                ["alive"] = session.IsAlive ? "运行中" : "已结束",
                ["core"] = session.ActiveCoreConfig,
                ["created"] = "—",
                ["rounds"] = SessionsProvider.GetRoundCount(session).ToString(),
                ["parent"] = session.ParentSessionId ?? "—",
                ["children"] = session.ChildSessionIds.Count > 0
                    ? string.Join(", ", session.ChildSessionIds)
                    : "—",
                ["channel"] = session.ConnectedChannel.CurrentChannelId > 0
                    ? session.ConnectedChannel.CurrentChannelId.ToString()
                    : "—",
                ["pool"] = pool == null ? "—" : FormatPool(pool),
                ["compression"] = "—",
            };
            return Task.FromResult(new DataResult { Data = data });
        }

        // 归档
        var dir = Path.Combine(PathConfig.StoragePath, "Sessions", sessionId);
        if (!Directory.Exists(dir))
            return Task.FromResult(new DataResult { Data = new JsonObject { ["status"] = "会话不存在" } });

        try
        {
            SessionStateProbe? state = null;
            SessionSpecProbe? spec = null;
            var statePath = Path.Combine(dir, "state.json");
            var specPath = Path.Combine(dir, "spec.json");
            if (File.Exists(statePath))
                state = JsonConvert.DeserializeObject<SessionStateProbe>(File.ReadAllText(statePath));
            if (File.Exists(specPath))
                spec = JsonConvert.DeserializeObject<SessionSpecProbe>(File.ReadAllText(specPath));

            var data = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["type"] = spec?.Type ?? "—",
                ["status"] = state?.Status ?? "Crashed",
                ["alive"] = "已归档",
                ["core"] = state?.ActiveCoreConfig ?? "—",
                ["created"] = state?.CreatedAt is DateTime dt && dt != default
                    ? Util.TimeUtil.ToLocalSafe(dt).ToString("yyyy-MM-dd HH:mm:ss")
                    : "—",
                ["rounds"] = state?.RoundCount.ToString() ?? "—",
                ["parent"] = state?.EffectiveParentId ?? spec?.ParentSessionId ?? "—",
                ["children"] = state?.ChildSessionIds?.Length > 0
                    ? string.Join(", ", state.ChildSessionIds)
                    : "—",
                ["channel"] = state?.ConnectedChannelId ?? "—",
                ["pool"] = "（已归档，资源池已释放）",
                ["compression"] = state?.CompressionTier ?? "None",
            };
            return Task.FromResult(new DataResult { Data = data });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DataResult
            {
                Data = new JsonObject { ["status"] = "存档解析失败", ["sessionId"] = sessionId, ["core"] = ex.Message }
            });
        }
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false, Message = "不支持操作" });

    private static string FormatPool(SessionPool pool)
    {
        var parts = new List<string>();
        if (pool.IsRoot)
            parts.Add($"频道池 #{pool.ChannelId}");
        else
            parts.Add("子会话池");
        parts.Add($"活跃={pool.ActiveSessionId}");
        if (pool.ListenSessionIds.Count > 0)
            parts.Add($"旁听={string.Join(",", pool.ListenSessionIds)}");
        if (pool.ChildPools.Count > 0)
            parts.Add($"子池={pool.ChildPools.Count}");
        return string.Join(" | ", parts);
    }
}

/// <summary>会话组件与端口详情。</summary>
internal class SessionComponentSource : IDataSource
{
    private readonly MasterEngine _engine;
    public SessionComponentSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var sessionId = query?.RouteParams?.GetValueOrDefault("sessionId") ?? "";
        if (string.IsNullOrEmpty(sessionId))
            return Task.FromResult(new DataResult { Data = new JsonObject() });

        var se = _engine.GetActiveEnginesSnapshot().OfType<SessionEngine>().FirstOrDefault();
        var session = se?.GetSession(sessionId) as RuntimeSession;
        if (session != null)
        {
            return Task.FromResult(new DataResult
            {
                Data = new JsonObject
                {
                    ["components"] = session.ComponentIds.Count > 0
                        ? string.Join("\n", session.ComponentIds)
                        : "—",
                    ["ports"] = $"连接频道: {(session.ConnectedChannel.CurrentChannelId > 0 ? session.ConnectedChannel.CurrentChannelId.ToString() : "—")}",
                    ["note"] = "运行中会话的组件/端口是其构建时的快照。",
                }
            });
        }

        try
        {
            var specPath = Path.Combine(PathConfig.StoragePath, "Sessions", sessionId, "spec.json");
            if (!File.Exists(specPath))
                return Task.FromResult(new DataResult { Data = new JsonObject { ["components"] = "存档不存在" } });

            var spec = JsonConvert.DeserializeObject<SessionSpecProbe>(File.ReadAllText(specPath));
            var components = spec?.ResolvedComponentIds?.Length > 0
                ? spec.ResolvedComponentIds
                : spec?.AdditionalComponents ?? Array.Empty<string>();
            var ports = spec?.InputPorts != null && spec.InputPorts.Count > 0
                ? string.Join("\n", spec.InputPorts.Select(kv => $"{kv.Key}: channel={kv.Value.ChannelId} stream={kv.Value.SubStream} weight={kv.Value.Weight}"))
                : "—";

            return Task.FromResult(new DataResult
            {
                Data = new JsonObject
                {
                    ["components"] = components.Length > 0 ? string.Join("\n", components) : "—",
                    ["ports"] = ports,
                    ["note"] = "来自 spec.json（不可变存档）。",
                }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DataResult { Data = new JsonObject { ["components"] = $"解析失败: {ex.Message}" } });
        }
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false, Message = "不支持操作" });
}

internal class SessionStateProbe
{
    [JsonProperty("sessionId")] public string? SessionId { get; set; }
    [JsonProperty("status")] public string? Status { get; set; }
    [JsonProperty("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonProperty("roundCount")] public int RoundCount { get; set; }
    [JsonProperty("compressionTier")] public string? CompressionTier { get; set; }
    [JsonProperty("activeCoreConfig")] public string? ActiveCoreConfig { get; set; }
    [JsonProperty("connectedChannelId")] public string? ConnectedChannelId { get; set; }
    [JsonProperty("childSessionIds")] public string[]? ChildSessionIds { get; set; }
    [JsonProperty("effectiveParentId")] public string? EffectiveParentId { get; set; }
}

internal class SessionSpecProbe
{
    [JsonProperty("type")] public string? Type { get; set; }
    [JsonProperty("additionalComponents")] public string[]? AdditionalComponents { get; set; }
    [JsonProperty("resolvedComponentIds")] public string[]? ResolvedComponentIds { get; set; }
    [JsonProperty("inputPorts")] public Dictionary<string, PortSpec>? InputPorts { get; set; }
    [JsonProperty("parentSessionId")] public string? ParentSessionId { get; set; }
}

/// <summary>
/// 消息注入表单。
/// </summary>
internal class MessageInjectSource : IDataSource
{
    private readonly MasterEngine _engine;
    public MessageInjectSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;

    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var formDefaults = new JsonObject
        {
            ["channelId"] = "",
            ["content"] = "",
            ["tag"] = "normal"
        };
        return Task.FromResult(new DataResult { Data = formDefaults });
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action != "save" || data is not JsonObject obj)
            return new ActionResult { Success = false, Message = "无效请求" };

        var channelIdStr = obj["channelId"]?.ToString() ?? "";
        var content = obj["content"]?.ToString() ?? "";
        var tag = obj["tag"]?.ToString() ?? "normal";

        if (!int.TryParse(channelIdStr, out var channelId) || channelId <= 0)
            return new ActionResult { Success = false, Message = "请输入有效的频道ID" };

        if (string.IsNullOrWhiteSpace(content))
            return new ActionResult { Success = false, Message = "消息内容不能为空" };

        var me = _engine.GetActiveEnginesSnapshot().OfType<MessageEngine>().FirstOrDefault();
        if (me == null)
            return new ActionResult { Success = false, Message = "MessageEngine 不可用" };

        var msg = new EngineIncomingMessage
        {
            ChannelId = channelId,
            MessageId = "inject-" + Guid.NewGuid().ToString("N")[..8],
            Content = content,
            SenderUserId = 0,
            Tag = tag,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            await me.AcceptIncoming(msg);
            return new ActionResult { Success = true, Message = $"消息已注入频道 #{channelId} (tag: {tag})" };
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Message = $"注入失败: {ex.Message}" };
        }
    }
}
