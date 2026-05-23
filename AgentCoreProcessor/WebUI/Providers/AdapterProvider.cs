using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.WebUI;
using ActionResult = AgentLilara.PluginSDK.WebUI.ActionResult;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class AdapterProvider : IWebUIProvider
{
    private readonly MasterEngine _engine;
    private AdapterManager Adapters => _engine.Adapters;

    public string Id => "adapter-provider";
    public string DisplayName => "适配器";

    public AdapterProvider(MasterEngine engine)
    {
        _engine = engine;
        Pages = BuildPages();
    }

    public IReadOnlyList<PageDefinition> Pages { get; }

    private static string SrcId(string platform, string suffix) => $"adapter-{platform}-{suffix}";

    private static readonly string[] OneBotActions = new[]
    {
        "get_group_list", "get_group_member_list", "get_friend_list",
        "recall", "poke", "set_group_card"
    };

    private List<PageDefinition> BuildPages()
    {
        var pages = new List<PageDefinition>();

        // ═══ 总览 ═══
        pages.Add(new()
        {
            Route = "adapters",
            Meta = new PageMeta { Title = "适配器总览", Icon = "bi-plug", Group = "适配器", Order = 10 },
            Cards = new List<CardDefinition>
            {
                new()
                {
                    Id = "adapter-stats", Type = CardType.Status, DataSourceId = "adapter-stats", Title = "适配器统计",
                    Schema = new StatusSchema
                    {
                        Fields = new()
                        {
                            new() { Field = "registered", Label = "已注册" },
                            new() { Field = "enabled", Label = "已启用" },
                            new() { Field = "connected", Label = "已连接" },
                            new() { Field = "traffic", Label = "总收发" }
                        }
                    },
                    Layout = new() { PreferredCols = 6 }
                },
                new()
                {
                    Id = "adapter-list", Type = CardType.Table, DataSourceId = "adapter-list", Title = "适配器列表",
                    Schema = new TableSchema
                    {
                        Columns = new()
                        {
                            new() { Field = "id", Header = "ID", Width = "120px" },
                            new() { Field = "platform", Header = "平台", Width = "80px", Format = ColumnFormat.Badge },
                            new() { Field = "state", Header = "状态", Width = "90px" },
                            new() { Field = "rx", Header = "RX", Width = "70px" },
                            new() { Field = "tx", Header = "TX", Width = "70px" }
                        },
                        RowActions = new()
                        {
                            new() { Id = "toggle", Label = "启用/禁用" },
                            new() { Id = "delete", Label = "删除", Danger = true }
                        }
                    },
                    Layout = new() { PreferredCols = 6 }
                },
                new()
                {
                    Id = "adapter-create", Type = CardType.Form, DataSourceId = "adapter-create", Title = "新建适配器",
                    Schema = new FormSchema
                    {
                        Fields = new()
                        {
                            new() { Field = "type", Label = "类型", Type = FormFieldType.Select, Required = true,
                                Options = new() { new() { Value = "onebot", Label = "OneBot" }, new() { Value = "file", Label = "File" } } },
                            new() { Field = "id", Label = "ID", Type = FormFieldType.Text, Placeholder = "唯一标识", Required = true }
                        }
                    },
                    Layout = new() { PreferredCols = 6 }
                }
            },
            DataSources = new List<DataSourceDefinition>
            {
                new() { Id = "adapter-stats",  Source = new AdapterStatsSource(Adapters) },
                new() { Id = "adapter-list",   Source = new AdapterListSource(Adapters) },
                new() { Id = "adapter-create", Source = new AdapterCreateSource(Adapters) }
            }
        });

        // ═══ OneBot 详情 ═══
        pages.Add(BuildPlatformPage("onebot", "OneBot 适配器", new()
        {
            // 实例选择器
            new()
            {
                Id = "onebot-picker",
                Type = CardType.Table,
                DataSourceId = SrcId("onebot", "picker"),
                Title = "实例",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "100px" },
                        new() { Field = "state", Header = "状态", Width = "90px" },
                        new() { Field = "rx", Header = "RX", Width = "60px" },
                        new() { Field = "tx", Header = "TX", Width = "60px" }
                    }
                },
                LinkEvent = "adapter-selected",
                Layout = new() { Order = 0, PreferredCols = 12 }
            },
            // 运行状态
            new()
            {
                Id = "onebot-status",
                Type = CardType.Status,
                DataSourceId = SrcId("onebot", "status"),
                Title = "运行状态",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "platform", Label = "平台" },
                        new() { Field = "state", Label = "状态", Type = StatusFieldType.Indicator },
                        new() { Field = "rx", Label = "接收" },
                        new() { Field = "tx", Label = "发送" },
                        new() { Field = "started", Label = "启动时间" },
                        new() { Field = "reconnect", Label = "重连次数" },
                        new() { Field = "last_error", Label = "最后错误", IsMultiline = true }
                    }
                },
                ListenEvent = "adapter-selected",
                Layout = new() { Order = 1, PreferredCols = 4 }
            },
            // 控制面板
            new()
            {
                Id = "onebot-control",
                Type = CardType.Status,
                DataSourceId = SrcId("onebot", "control"),
                Title = "控制",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "auto_start", Label = "自动启动" },
                        new() { Field = "debug", Label = "调试模式" }
                    },
                    Actions = new()
                    {
                        new() { Id = "enable", Label = "启用" },
                        new() { Id = "disable", Label = "禁用" },
                        new() { Id = "toggle_auto", Label = "切换自动启动" },
                        new() { Id = "toggle_debug", Label = "切换调试模式" }
                    }
                },
                ListenEvent = "adapter-selected",
                Layout = new() { Order = 2, PreferredCols = 4 }
            },
            // 连接配置
            new()
            {
                Id = "onebot-config",
                Type = CardType.Form,
                DataSourceId = SrcId("onebot", "config"),
                Title = "连接配置",
                Schema = new FormSchema
                {
                    Fields = new()
                    {
                        new() { Field = "ws_url", Label = "WS URL", Type = FormFieldType.Text, Required = true },
                        new() { Field = "token", Label = "Token", Type = FormFieldType.Text },
                        new() { Field = "filter_mode", Label = "过滤模式", Type = FormFieldType.Select,
                            Options = new() { new() { Value = "none", Label = "无" }, new() { Value = "whitelist", Label = "白名单" }, new() { Value = "blacklist", Label = "黑名单" } } },
                        new() { Field = "bot_names", Label = "机器人名（逗号分隔）", Type = FormFieldType.Text },
                        new() { Field = "filter_list", Label = "过滤列表（换行分隔）", Type = FormFieldType.TextArea }
                    }
                },
                ListenEvent = "adapter-selected",
                Layout = new() { Order = 3, PreferredCols = 4 }
            }
        }, OneBotActions));

        // ═══ File 详情 ═══
        pages.Add(BuildPlatformPage("file", "File 适配器", new()
        {
            new()
            {
                Id = "file-picker",
                Type = CardType.Table,
                DataSourceId = SrcId("file", "picker"),
                Title = "实例",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "100px" },
                        new() { Field = "state", Header = "状态", Width = "90px" },
                        new() { Field = "rx", Header = "RX", Width = "60px" },
                        new() { Field = "tx", Header = "TX", Width = "60px" }
                    }
                },
                LinkEvent = "adapter-selected",
                Layout = new() { Order = 0, PreferredCols = 12 }
            },
            new()
            {
                Id = "file-status",
                Type = CardType.Status,
                DataSourceId = SrcId("file", "status"),
                Title = "运行状态",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "platform", Label = "平台" },
                        new() { Field = "state", Label = "状态", Type = StatusFieldType.Indicator },
                        new() { Field = "rx", Label = "接收" },
                        new() { Field = "tx", Label = "发送" },
                        new() { Field = "started", Label = "启动时间" }
                    }
                },
                ListenEvent = "adapter-selected",
                Layout = new() { Order = 1, PreferredCols = 4 }
            },
            new()
            {
                Id = "file-config",
                Type = CardType.Form,
                DataSourceId = SrcId("file", "config"),
                Title = "目录配置",
                Schema = new FormSchema
                {
                    Fields = new()
                    {
                        new() { Field = "input_dir", Label = "输入目录", Type = FormFieldType.Text },
                        new() { Field = "output_dir", Label = "输出目录", Type = FormFieldType.Text },
                        new() { Field = "poll_ms", Label = "轮询间隔 (ms)", Type = FormFieldType.Number }
                    }
                },
                ListenEvent = "adapter-selected",
                Layout = new() { Order = 2, PreferredCols = 4 }
            }
        }, null));

        return pages;
    }

    private PageDefinition BuildPlatformPage(string platform, string title, List<CardDefinition> baseCards, string[]? actionNames)
    {
        var cards = new List<CardDefinition>(baseCards);
        var dataSources = new List<DataSourceDefinition>
        {
            new() { Id = SrcId(platform, "picker"), Source = new PlatformPickerSource(Adapters, platform) },
            new() { Id = SrcId(platform, "status"), Source = new AdapterStatusSource(Adapters) },
            new() { Id = SrcId(platform, "control"), Source = new AdapterControlSource(Adapters) },
            new() { Id = SrcId(platform, "config"), Source = new AdapterConfigSource(Adapters) }
        };

        if (actionNames != null)
        {
            int actionOrder = baseCards.Count;
            foreach (var name in actionNames)
            {
                var def = GetActionDef(name);
                var dsId = SrcId(platform, $"action-{name}");
                cards.Add(new()
                {
                    Id = $"{platform}-action-{name}",
                    Type = CardType.Action,
                    DataSourceId = dsId,
                    Title = def.Label,
                    Schema = new ActionCardSchema
                    {
                        ActionId = name,
                        ActionLabel = def.Label,
                        Description = def.Description,
                        Params = def.Params,
                        SubmitLabel = def.SubmitLabel
                    },
                    ListenEvent = "adapter-selected",
                    Layout = new() { Order = actionOrder++, PreferredCols = 4 }
                });
                dataSources.Add(new() { Id = dsId, Source = new ActionExecSource(Adapters, platform, name) });
            }
        }

        return new()
        {
            Route = $"adapters/{platform}",
            Meta = new PageMeta { Title = title, Icon = "bi-plug", Group = "适配器", Order = platform == "onebot" ? 11 : 12 },
            Cards = cards.AsReadOnly(),
            DataSources = dataSources.AsReadOnly()
        };
    }

    private static (string Label, string Description, List<ActionParamDef> Params, string SubmitLabel) GetActionDef(string name) => name switch
    {
        "get_group_list" => ("获取群列表", "返回所有已加入的群。", new(), "获取"),
        "get_group_member_list" => ("获取群成员列表", "返回指定群的成员列表。", new()
        {
            new() { Name = "group_id", Label = "群号", Required = true }
        }, "获取"),
        "get_friend_list" => ("获取好友列表", "返回好友列表。", new(), "获取"),
        "recall" => ("撤回消息", "撤回指定消息。", new()
        {
            new() { Name = "message_id", Label = "消息ID", Required = true }
        }, "撤回"),
        "poke" => ("戳一戳", "向指定用户发送戳一戳。", new()
        {
            new() { Name = "user_id", Label = "用户QQ", Required = true },
            new() { Name = "group_id", Label = "群号（可选）", Required = false }
        }, "发送"),
        "set_group_card" => ("设置群名片", "修改指定群成员的群名片。", new()
        {
            new() { Name = "group_id", Label = "群号", Required = true },
            new() { Name = "user_id", Label = "用户QQ", Required = true },
            new() { Name = "card", Label = "新名片", Required = true }
        }, "设置"),
        _ => (name, "", new(), "执行")
    };
}

// ════════════════════════════════════════════════════════
// 数据源
// ════════════════════════════════════════════════════════

internal class AdapterStatsSource : IDataSource
{
    private readonly AdapterManager _adapters;
    public bool SupportsPush => true;
    public AdapterStatsSource(AdapterManager adapters) { _adapters = adapters; }

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        void OnChanged() => callback(null);
        _adapters.OnAdaptersChanged += OnChanged;
        return new ActionDisposable(() => _adapters.OnAdaptersChanged -= OnChanged);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = _adapters.GetAllStatuses();
        var connected = all.Count(s => s.State == AdapterConnectionState.Connected);
        var totalRx = all.Sum(s => (long)s.MessagesReceived);
        var totalTx = all.Sum(s => (long)s.MessagesSent);
        return Task.FromResult(new DataResult
        {
            Data = new JsonObject
            {
                ["registered"] = all.Count,
                ["enabled"] = all.Count(s => s.Enabled),
                ["connected"] = connected,
                ["traffic"] = $"RX {totalRx:N0} / TX {totalTx:N0}"
            }
        });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class AdapterListSource : IDataSource
{
    private readonly AdapterManager _adapters;
    public bool SupportsPush => true;

    public AdapterListSource(AdapterManager adapters) { _adapters = adapters; }

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        void OnChanged() => callback(null);
        _adapters.OnAdaptersChanged += OnChanged;
        return new ActionDisposable(() => _adapters.OnAdaptersChanged -= OnChanged);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var arr = new JsonArray();
        foreach (var s in _adapters.GetAllStatuses())
        {
            arr.Add(new JsonObject
            {
                ["id"] = s.Id,
                ["platform"] = s.Platform,
                ["state"] = s.State.ToString(),
                ["rx"] = s.MessagesReceived,
                ["tx"] = s.MessagesSent,
                ["_link"] = $"/p/adapters/{s.Platform.ToLowerInvariant()}"
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (data == null) return new ActionResult { Success = false };
        var id = data["id"]?.ToString();
        if (string.IsNullOrEmpty(id)) return new ActionResult { Success = false };

        switch (action)
        {
            case "toggle":
                if (_adapters.IsEnabled(id))
                    return new ActionResult { Success = await _adapters.DisableAsync(id) };
                else
                    return new ActionResult { Success = await _adapters.EnableAsync(id) };
            case "delete":
                return new ActionResult { Success = await _adapters.RemoveAsync(id) };
            default:
                return new ActionResult { Success = false, Message = $"未知操作: {action}" };
        }
    }
}

internal class AdapterCreateSource : IDataSource
{
    private readonly AdapterManager _adapters;
    public bool SupportsPush => false;
    public AdapterCreateSource(AdapterManager adapters) { _adapters = adapters; }

    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
        => Task.FromResult(new DataResult { Data = null });

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (data == null) return new ActionResult { Success = false, Message = "缺少参数" };
        var type = data["type"]?.ToString();
        var id = data["id"]?.ToString();
        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(id))
            return new ActionResult { Success = false, Message = "类型和ID不能为空" };

        var config = new AdapterInstanceConfig { Id = id, Type = type, Enabled = true };
        var ok = await _adapters.AddAsync(config);
        return new ActionResult { Success = ok, Message = ok ? $"已创建 {id}" : $"创建失败，ID \"{id}\" 可能已存在" };
    }
}

internal class PlatformPickerSource : IDataSource
{
    private readonly AdapterManager _adapters;
    private readonly string _platform;
    public bool SupportsPush => true;

    public PlatformPickerSource(AdapterManager adapters, string platform)
    {
        _adapters = adapters;
        _platform = platform;
    }

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        void OnChanged() => callback(null);
        _adapters.OnAdaptersChanged += OnChanged;
        return new ActionDisposable(() => _adapters.OnAdaptersChanged -= OnChanged);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var arr = new JsonArray();
        foreach (var s in _adapters.GetAllStatuses()
            .Where(s => s.Platform.Equals(_platform, StringComparison.OrdinalIgnoreCase)))
        {
            arr.Add(new JsonObject
            {
                ["id"] = s.Id,
                ["state"] = s.State.ToString(),
                ["rx"] = s.MessagesReceived,
                ["tx"] = s.MessagesSent
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class AdapterStatusSource : IDataSource
{
    private readonly AdapterManager _adapters;
    public bool SupportsPush => true;
    private string? _selectedId;

    public AdapterStatusSource(AdapterManager adapters) { _adapters = adapters; }

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        void OnChanged() { if (_selectedId != null) callback(null); }
        _adapters.OnAdaptersChanged += OnChanged;
        return new ActionDisposable(() => _adapters.OnAdaptersChanged -= OnChanged);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var id = query?.Extra?["id"]?.ToString() ?? _selectedId;
        _selectedId = id;
        if (string.IsNullOrEmpty(id))
            return Task.FromResult(new DataResult { Data = new JsonObject { ["state"] = "未选择实例" } });

        var s = _adapters.GetAdapterById(id)?.GetStatus();
        if (s == null)
            return Task.FromResult(new DataResult { Data = new JsonObject { ["state"] = "实例不存在" } });

        return Task.FromResult(new DataResult
        {
            Data = new JsonObject
            {
                ["platform"] = s.Platform,
                ["state"] = s.State.ToString(),
                ["rx"] = s.MessagesReceived,
                ["tx"] = s.MessagesSent,
                ["started"] = s.StartedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                ["reconnect"] = s.ReconnectCount,
                ["last_error"] = s.LastError ?? ""
            }
        });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class AdapterControlSource : IDataSource
{
    private readonly AdapterManager _adapters;
    public bool SupportsPush => true;
    private string? _selectedId;

    public AdapterControlSource(AdapterManager adapters) { _adapters = adapters; }

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        void OnChanged() { if (_selectedId != null) callback(null); }
        _adapters.OnAdaptersChanged += OnChanged;
        return new ActionDisposable(() => _adapters.OnAdaptersChanged -= OnChanged);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var id = query?.Extra?["id"]?.ToString() ?? _selectedId;
        _selectedId = id;
        if (string.IsNullOrEmpty(id))
            return Task.FromResult(new DataResult { Data = new JsonObject { ["auto_start"] = "-" } });

        var cfg = _adapters.GetConfigById(id);
        var enabled = _adapters.IsEnabled(id);
        return Task.FromResult(new DataResult
        {
            Data = new JsonObject
            {
                ["auto_start"] = cfg?.AutoStart == true ? "是" : "否",
                ["debug"] = cfg?.AutoStartDebug == true ? "是" : "否",
                ["_disabled_actions"] = new JsonArray(enabled ? (JsonValue)"enable" : (JsonValue)"disable")
            }
        });
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var id = _selectedId;
        if (string.IsNullOrEmpty(id)) return new ActionResult { Success = false, Message = "未选择实例" };

        switch (action)
        {
            case "enable":
                return new ActionResult { Success = await _adapters.EnableAsync(id), Message = "已启用" };
            case "disable":
                return new ActionResult { Success = await _adapters.DisableAsync(id), Message = "已禁用" };
            case "toggle_auto":
                var cfg = _adapters.GetConfigById(id);
                if (cfg != null) { cfg.AutoStart = !cfg.AutoStart; _adapters.UpdateConfig(cfg); }
                return new ActionResult { Success = true, Message = "已切换" };
            case "toggle_debug":
                var c = _adapters.GetConfigById(id);
                if (c != null) { c.AutoStartDebug = !c.AutoStartDebug; _adapters.UpdateConfig(c); }
                return new ActionResult { Success = true, Message = "已切换" };
            default:
                return new ActionResult { Success = false, Message = $"未知操作: {action}" };
        }
    }
}

internal class AdapterConfigSource : IDataSource
{
    private readonly AdapterManager _adapters;
    public bool SupportsPush => false;
    private string? _selectedId;

    public AdapterConfigSource(AdapterManager adapters) { _adapters = adapters; }

    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var id = query?.Extra?["id"]?.ToString() ?? _selectedId;
        _selectedId = id;
        if (string.IsNullOrEmpty(id))
            return Task.FromResult(new DataResult { Data = null });

        var cfg = _adapters.GetConfigById(id);
        if (cfg == null)
            return Task.FromResult(new DataResult { Data = null });

        var s = cfg.Settings;
        return Task.FromResult(new DataResult
        {
            Data = new JsonObject
            {
                ["ws_url"] = s["WsUrl"]?.ToString() ?? s["ws_url"]?.ToString() ?? "",
                ["token"] = s["Token"]?.ToString() ?? s["token"]?.ToString() ?? "",
                ["filter_mode"] = s["FilterMode"]?.ToString() ?? s["filter_mode"]?.ToString() ?? "none",
                ["bot_names"] = s["BotNames"]?.ToString() ?? s["bot_names"]?.ToString() ?? "",
                ["filter_list"] = FormatFilterList(s),
                ["input_dir"] = s["input_dir"]?.ToString() ?? "",
                ["output_dir"] = s["output_dir"]?.ToString() ?? "",
                ["poll_ms"] = s["poll_ms"]?.ToString() ?? s["PollIntervalMs"]?.ToString() ?? "2000"
            }
        });
    }

    private static string FormatFilterList(Newtonsoft.Json.Linq.JObject s)
    {
        var wl = s["Whitelist"] ?? s["whitelist"];
        var bl = s["Blacklist"] ?? s["blacklist"];
        var target = s["FilterMode"]?.ToString() == "blacklist" ? bl : wl;
        if (target is Newtonsoft.Json.Linq.JArray arr)
            return string.Join("\n", arr.Select(x => x.ToString()));
        if (target is Newtonsoft.Json.Linq.JArray arr2)
            return string.Join("\n", arr2.Select(x => x.ToString()));
        return "";
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var id = _selectedId;
        if (string.IsNullOrEmpty(id)) return new ActionResult { Success = false, Message = "未选择实例" };
        if (data == null) return new ActionResult { Success = false, Message = "缺少配置数据" };

        var cfg = _adapters.GetConfigById(id);
        if (cfg == null) return new ActionResult { Success = false, Message = "配置不存在" };

        var s = cfg.Settings;
        s["WsUrl"] = data["ws_url"]?.ToString() ?? "";
        s["Token"] = data["token"]?.ToString() ?? "";
        s["FilterMode"] = data["filter_mode"]?.ToString() ?? "none";
        s["BotNames"] = data["bot_names"]?.ToString() ?? "";

        var filterListStr = data["filter_list"]?.ToString() ?? "";
        var filterArr = new Newtonsoft.Json.Linq.JArray(
            filterListStr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrEmpty(line)));
        if (data["filter_mode"]?.ToString() == "blacklist")
            s["Blacklist"] = filterArr;
        else
            s["Whitelist"] = filterArr;

        _adapters.UpdateConfig(cfg);
        return new ActionResult { Success = true, Message = "配置已保存" };
    }
}

internal class ActionExecSource : IDataSource
{
    private readonly AdapterManager _adapters;
    private readonly string _platform;
    private readonly string _actionName;
    public bool SupportsPush => false;
    private string? _selectedId;

    public ActionExecSource(AdapterManager adapters, string platform, string actionName)
    {
        _adapters = adapters;
        _platform = platform;
        _actionName = actionName;
    }

    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var id = query?.Extra?["id"]?.ToString() ?? _selectedId;
        _selectedId = id;
        return Task.FromResult(new DataResult { Data = null });
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var id = _selectedId;
        if (string.IsNullOrEmpty(id)) return new ActionResult { Success = false, Message = "未选择适配器实例" };
        if (data == null) return new ActionResult { Success = false, Message = "缺少请求数据" };

        var paramObj = data["params"] as JsonObject;
        var parameters = new Dictionary<string, string>();
        if (paramObj != null)
        {
            foreach (var kv in paramObj)
                parameters[kv.Key] = kv.Value?.ToString() ?? "";
        }

        var execResult = await _adapters.ExecuteActionAsync(_platform, "", _actionName, parameters);
        if (!execResult.Success)
            return new ActionResult { Success = false, Message = execResult.Error ?? "执行失败" };

        return new ActionResult
        {
            Success = true,
            Message = "执行完成",
            Data = new JsonObject { ["_result"] = execResult.Result ?? "OK" }
        };
    }
}

internal class ActionDisposable : IDisposable
{
    private readonly Action _action;
    public ActionDisposable(Action action) => _action = action;
    public void Dispose() => _action();
}
