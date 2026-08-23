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
        BuildInjectPage()
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
                Layout = new CardLayout { PreferredCols = 6, GridColumnStart = 1 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "sessions-active", Source = new SessionsActiveSource(_engine) },
            new() { Id = "core-switch", Source = new CoreSwitchSource(_engine) }
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
            new() { Id = "sessions-history", Source = new SessionsHistorySource(_engine) }
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
            new() { Id = "sessions-inject", Source = new MessageInjectSource(_engine) }
        }
    };

    // ── 辅助：获取会话轮次 ──

    internal static int GetRoundCount(IRuntimeSession session)
    {
        try
        {
            var field = typeof(RuntimeSession).GetField("_roundCount",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
                return (int)(field.GetValue(session) ?? 0);
        }
        catch { }
        return 0;
    }

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

        if (arr.Count == 0)
        {
            arr.Add(new JsonObject
            {
                ["sessionId"] = "（无活跃会话）",
                ["status"] = "—",
                ["components"] = "—",
                ["parent"] = "—",
                ["rounds"] = "—"
            });
        }

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
        var entries = new List<(string sessionId, string status, string components, string createdAt)>();

        foreach (var dir in dirs)
        {
            var metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath)) continue;

            try
            {
                var json = File.ReadAllText(metaPath);
                var meta = JsonConvert.DeserializeAnonymousType(json, new
                {
                    status = "",
                    componentIds = new List<string>(),
                    createdAt = DateTime.MinValue
                });

                if (meta == null) continue;

                var sessionId = Path.GetFileName(dir);
                var components = meta.componentIds?.Count > 0
                    ? string.Join(", ", meta.componentIds)
                    : "—";
                var createdAt = meta.createdAt != DateTime.MinValue
                    ? meta.createdAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    : "—";

                entries.Add((sessionId, meta.status, components, createdAt));
            }
            catch { /* skip corrupt meta */ }
        }

        // 按创建时间倒序（从文件修改时间推断）
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
                ["_link"] = $"/p/sessions/{entry.sessionId}"
            });
        }

        return Task.FromResult(new DataResult { Data = arr, TotalCount = entries.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
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
