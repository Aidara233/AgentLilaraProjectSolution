using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class PluginsProvider : IWebUIProvider
{
    public string Id => "plugins";
    public string DisplayName => "插件";

    private readonly MasterEngine _engine;

    public PluginsProvider(MasterEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<PageDefinition> Pages => new List<PageDefinition>
    {
        BuildPluginListPage(),
        BuildPluginDetailPage(),
        BuildToolsPage(),
        BuildSessionTypesPage(),
        BuildMcpNavPage(),
    };

    // ================ 1. 插件清单 ================

    private PageDefinition BuildPluginListPage() => new()
    {
        Route = "plugins",
        Meta = new PageMeta { Title = "插件清单", Icon = "bi-puzzle", Group = "插件", Order = 90 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "plugin-list", Type = CardType.Table, DataSourceId = "plugin-list", Title = "已加载插件",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "fileName", Header = "文件名" },
                        new() { Field = "toolCount", Header = "工具", Width = "60px", Sortable = false },
                        new() { Field = "componentCount", Header = "组件", Width = "60px", Sortable = false },
                        new() { Field = "providerCount", Header = "Provider", Width = "80px", Sortable = false },
                        new() { Field = "injectCount", Header = "注入", Width = "60px", Sortable = false },
                        new() { Field = "lifecycleCount", Header = "生命周期", Width = "80px", Sortable = false },
                    },
                    Searchable = false, Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 8 }
            },
            new()
            {
                Id = "plugin-reload", Type = CardType.Action, DataSourceId = "plugin-reload", Title = "全部热重载",
                Schema = new ActionCardSchema
                {
                    ActionId = "reload",
                    ActionLabel = "热重载全部插件",
                    Description = "卸载所有插件并重新扫描 Plugins/ 目录加载",
                    SubmitLabel = "热重载",
                    Params = new()
                    {
                        new() { Name = "confirm", Label = "确认", Type = "select", Required = true,
                            Options = new() { new() { Value = "yes", Label = "是，执行热重载" } } }
                    }
                },
                Layout = new CardLayout { PreferredCols = 4 },
                AffectedDataSourceIds = new[] { "plugin-list" }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "plugin-list", Source = new PluginListSource(_engine), Paging = DataPagingMode.Client },
            new() { Id = "plugin-reload", Source = new PluginReloadSource(_engine) },
        }
    };

    /// <summary>
    /// MCP 状态页是独立 Razor 组件（/mcp），这里只注册导航入口；
    /// DynamicPage 遇到空 Cards 时需要显式重定向。
    /// </summary>
    private PageDefinition BuildMcpNavPage() => new()
    {
        Route = "mcp",
        Meta = new PageMeta { Title = "MCP", Icon = "bi-plug", Group = "插件", Order = 93 },
        Cards = new List<CardDefinition>(),
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "mcp-redirect", Factory = () => new RedirectDataSource("/mcp") }
        }
    };

    private sealed class RedirectDataSource : IDataSource
    {
        private readonly string _target;
        public RedirectDataSource(string target) => _target = target;
        public bool SupportsPush => false;
        public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

        public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
            => Task.FromResult(new DataResult
            {
                Data = new JsonObject(),
                Meta = new JsonObject { ["redirect"] = _target }
            });

        public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
            => Task.FromResult(new ActionResult { Success = false, Message = "只读导航入口" });
    }

    // ================ 2. 插件详情 ================

    private PageDefinition BuildPluginDetailPage() => new()
    {
        Route = "plugins/{name}",
        Meta = new PageMeta { Title = "插件详情", Icon = "bi-puzzle", Group = "插件", Order = 20, ShowInNav = false },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "plugin-info", Type = CardType.Status, DataSourceId = "plugin-info", Title = "插件信息",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "fileName", Label = "插件名" },
                        new() { Field = "version", Label = "版本" },
                        new() { Field = "description", Label = "描述" },
                        new() { Field = "author", Label = "作者" },
                        new() { Field = "filePath", Label = "路径" },
                        new() { Field = "toolCount", Label = "贡献工具" },
                        new() { Field = "componentCount", Label = "贡献组件" },
                        new() { Field = "providerCount", Label = "贡献 Provider" },
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "plugin-reload-single", Type = CardType.Action, DataSourceId = "plugin-reload-single", Title = "重载此插件",
                Schema = new ActionCardSchema
                {
                    ActionId = "reload",
                    ActionLabel = "热重载",
                    Description = "卸载并重新加载此插件 DLL，不影响其他插件。",
                    SubmitLabel = "重载",
                    Params = new()
                    {
                        new() { Name = "confirm", Label = "确认", Type = "select", Required = true,
                            Options = new() { new() { Value = "yes", Label = "是，执行重载" } } }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "plugin-tools", Type = CardType.Table, DataSourceId = "plugin-tools", Title = "工具列表",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "name", Header = "工具名" },
                        new() { Field = "description", Header = "描述" },
                        new() { Field = "group", Header = "分组", Width = "100px" },
                        new() { Field = "disabled", Header = "状态", Width = "80px" },
                    },
                    RowActions = new()
                    {
                        new() { Id = "disable", Label = "禁用", Danger = true },
                        new() { Id = "enable", Label = "启用" },
                    },
                    RowKeyField = "name",
                    Searchable = false, Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "plugin-components", Type = CardType.Table, DataSourceId = "plugin-components", Title = "组件列表",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "componentName", Header = "组件名" },
                        new() { Field = "scope", Header = "作用域", Width = "80px" },
                        new() { Field = "channel", Header = "频道", Width = "60px" },
                        new() { Field = "task", Header = "任务", Width = "60px" },
                    },
                    Searchable = false, Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "plugin-info", Factory = () => new PluginInfoSource(_engine) },
            new() { Id = "plugin-reload-single", Factory = () => new PluginReloadSingleSource(_engine) },
            new() { Id = "plugin-tools", Factory = () => new PluginToolsSource(_engine) },
            new() { Id = "plugin-components", Factory = () => new PluginComponentsSource(_engine) },
        }
    };

    // ================ 3. 工具列表 ================

    private PageDefinition BuildToolsPage() => new()
    {
        Route = "plugins/tools",
        Meta = new PageMeta { Title = "工具列表", Icon = "bi-tools", Group = "插件", Order = 91 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "all-tools", Type = CardType.Table, DataSourceId = "all-tools", Title = "全部工具",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "name", Header = "工具名" },
                        new() { Field = "description", Header = "描述" },
                        new() { Field = "source", Header = "来源", Width = "120px" },
                        new() { Field = "group", Header = "分组", Width = "100px" },
                        new() { Field = "disabled", Header = "状态", Width = "80px" },
                    },
                    Filters = new()
                    {
                        new() { Field = "source", Label = "来源", Options = BuildSourceFilterOptions(),
                            AllowEmpty = true },
                        new() { Field = "group", Label = "分组", Options = BuildGroupFilterOptions(),
                            AllowEmpty = true },
                    },
                    RowActions = new()
                    {
                        new() { Id = "disable", Label = "禁用", Danger = true },
                        new() { Id = "enable", Label = "启用" },
                    },
                    RowKeyField = "name",
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "all-tools", Source = new AllToolsSource(_engine), Paging = DataPagingMode.Client },
        }
    };

    // ================ 4. 会话类型（唯一权限源）================

    private PageDefinition BuildSessionTypesPage() => new()
    {
        Route = "plugins/types",
        Meta = new PageMeta { Title = "会话类型", Icon = "bi-shield-check", Group = "插件", Order = 92 },
        LayoutType = PageLayoutType.Sidebar,
        Cards = new List<CardDefinition>
        {
            // 左侧：会话类型列表
            new()
            {
                Id = "st-list", Type = CardType.Table, DataSourceId = "st-list", Title = "会话类型",
                LinkEvent = "st-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "type", Header = "类型" },
                        new() { Field = "mode", Header = "模式", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "toolCount", Header = "工具数", Width = "70px" },
                        new() { Field = "componentCount", Header = "组件数", Width = "70px" },
                    },
                    RowKeyField = "type",
                    Searchable = false, Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 3, GridColumnStart = 1 }
            },
            // 右侧：类型详情（模式切换 + 恢复默认）
            new()
            {
                Id = "st-detail", Type = CardType.Status, DataSourceId = "st-detail",
                Title = "会话类型详情", ListenEvent = "st-selected",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "type", Label = "类型" },
                        new() { Field = "core", Label = "默认核心" },
                        new() { Field = "mode", Label = "模式", Type = StatusFieldType.Badge },
                        new() { Field = "modeDesc", Label = "说明" },
                        new() { Field = "toolCount", Label = "工具清单数" },
                        new() { Field = "componentCount", Label = "组件清单数" },
                    },
                    Actions = new()
                    {
                        new() { Id = "toggle-mode", Label = "切换模式" },
                        new() { Id = "reset", Label = "恢复默认" },
                    }
                },
                Layout = new CardLayout { PreferredCols = 9, GridColumnStart = 4 }
            },
            // 右侧：组件清单（勾选 = 该类型装载）
            new()
            {
                Id = "st-components", Type = CardType.Table, DataSourceId = "st-components",
                Title = "组件清单（勾选 = 该类型装载；Global 组件恒装载，其工具由工具清单门控）",
                ListenEvent = "st-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "name", Header = "组件名" },
                        new() { Field = "scope", Header = "作用域", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "inList", Header = "装载", Width = "70px" },
                    },
                    RowActions = new()
                    {
                        new() { Id = "add", Label = "加入" },
                        new() { Id = "remove", Label = "移出", Danger = true },
                    },
                    RowKeyField = "name",
                    Searchable = true, Paginated = true, DefaultPageSize = 20
                },
                Layout = new CardLayout { PreferredCols = 9, GridColumnStart = 4 }
            },
            // 右侧：工具清单（白名单=仅勾选项可用；黑名单=勾选项禁用）
            new()
            {
                Id = "st-tools", Type = CardType.Table, DataSourceId = "st-tools",
                Title = "工具清单（白名单=仅勾选项可用；黑名单=勾选项禁用）", ListenEvent = "st-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "name", Header = "工具名" },
                        new() { Field = "description", Header = "描述" },
                        new() { Field = "source", Header = "来源", Width = "110px" },
                        new() { Field = "inList", Header = "清单", Width = "70px" },
                    },
                    Filters = new()
                    {
                        new() { Field = "source", Label = "来源", Options = BuildSourceFilterOptions(), AllowEmpty = true },
                    },
                    RowActions = new()
                    {
                        new() { Id = "add", Label = "加入" },
                        new() { Id = "remove", Label = "移出", Danger = true },
                    },
                    RowKeyField = "name",
                    Searchable = true, Paginated = true, DefaultPageSize = 30
                },
                Layout = new CardLayout { PreferredCols = 9, GridColumnStart = 4 }
            },
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "st-list", Source = new SessionTypesListSource() },
            new() { Id = "st-detail", Factory = () => new SessionTypeDetailSource() },
            new() { Id = "st-components", Factory = () => new SessionTypeComponentsSource(_engine) },
            new() { Id = "st-tools", Factory = () => new SessionTypeToolsSource(_engine) },
        }
    };

    // ---- 筛选选项构建 ----

    private List<SelectOption> BuildSourceFilterOptions()
    {
        var options = new List<SelectOption> { new() { Value = "__core__", Label = "核心工具" } };
        // Provider 在 engine.InitAsync 之前注册；此时 PluginLoader/组件宿主尚不存在。
        // 页面定义在每次枚举时重建，插件加载完成后再打开页面即可拿到完整来源列表。
        var loader = _engine.PluginLoader;
        if (loader == null) return options;

        foreach (var plugin in loader.LoadedPlugins)
        {
            options.Add(new SelectOption { Value = plugin.PluginId, Label = plugin.Manifest?.Name ?? plugin.PluginId });
        }
        return options;
    }

    private List<SelectOption> BuildGroupFilterOptions()
    {
        var groups = new HashSet<string>();
        foreach (var tool in ToolRegistry.All.Values)
        {
            var group = ToolRegistry.GetMeta(tool.Name)?.Group;
            if (!string.IsNullOrEmpty(group))
                groups.Add(group);
        }
        // 组件宿主可能尚未初始化（Provider 注册早于 InitAsync）。
        if (_engine.GlobalComponentHost != null)
        {
            foreach (var tool in _engine.GetAllComponentTools())
            {
                var meta = ToolRegistry.GetMeta(tool.Name)
                    ?? Attribute.GetCustomAttribute(tool.GetType(), typeof(ToolMetaAttribute)) as ToolMetaAttribute;
                if (!string.IsNullOrEmpty(meta?.Group))
                    groups.Add(meta.Group);
            }
        }
        return groups.OrderBy(g => g).Select(g => new SelectOption { Value = g, Label = g }).ToList();
    }
}

// ================ 插件清单数据源 ================

internal class PluginListSource : IDataSource
{
    private readonly MasterEngine _engine;
    public PluginListSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var plugins = _engine.PluginLoader.LoadedPlugins;
        var arr = new JsonArray();
        foreach (var plugin in plugins)
        {
            arr.Add(new JsonObject
            {
                ["id"] = plugin.PluginId,
                ["fileName"] = plugin.Manifest?.Name ?? plugin.PluginId,
                ["toolCount"] = plugin.ToolNames.Count,
                ["componentCount"] = plugin.ComponentNames.Count,
                ["providerCount"] = plugin.ProviderIds.Count,
                ["injectCount"] = plugin.InjectProviderNames.Count,
                ["lifecycleCount"] = plugin.LifecycleNames.Count,
                ["_link"] = $"/p/plugins/{plugin.PluginId}",
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });
}

// ================ 插件热重载数据源 ================

internal class PluginReloadSource : IDataSource
{
    private readonly MasterEngine _engine;
    public PluginReloadSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
        => Task.FromResult(new DataResult { Data = new JsonObject() });

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var actionId = data?["actionId"]?.ToString() ?? action;
        if (actionId != "reload")
            return Task.FromResult(new ActionResult { Success = false, Message = "未知操作" });

        try
        {
            _engine.PluginLoader.ReloadAll();
            return Task.FromResult(new ActionResult { Success = true, Message = "插件已全部热重载" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ActionResult { Success = false, Message = $"重载失败: {ex.Message}" });
        }
    }
}

// ================ 单插件热重载数据源 ================

internal class PluginReloadSingleSource : IDataSource
{
    private readonly MasterEngine _engine;
    private string _pluginName = "";
    public PluginReloadSingleSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        _pluginName = query?.RouteParams?.GetValueOrDefault("name") ?? "";
        return Task.FromResult(new DataResult { Data = new JsonObject() });
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var actionId = data?["actionId"]?.ToString() ?? action;
        if (actionId != "reload")
            return new ActionResult { Success = false, Message = "未知操作" };

        if (string.IsNullOrEmpty(_pluginName))
            return new ActionResult { Success = false, Message = "未指定插件" };

        try
        {
            await _engine.ReloadPluginAsync(_pluginName);
            return new ActionResult { Success = true, Message = $"插件 {_pluginName} 已重载" };
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Message = $"重载失败: {ex.Message}" };
        }
    }
}

// ================ 插件详情数据源 ================

internal class PluginInfoSource : IDataSource
{
    private readonly MasterEngine _engine;
    public PluginInfoSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var name = query?.RouteParams?.GetValueOrDefault("name") ?? "";
        var plugin = FindPlugin(name);
        if (plugin == null)
            return Task.FromResult(new DataResult { Data = new JsonObject { ["fileName"] = "未找到插件", ["filePath"] = name } });

        return Task.FromResult(new DataResult
        {
            Data = new JsonObject
            {
                ["fileName"] = plugin.Manifest?.Name ?? plugin.PluginId,
                ["filePath"] = plugin.FilePath,
                ["version"] = plugin.Manifest?.Version ?? "",
                ["description"] = plugin.Manifest?.Description ?? "",
                ["author"] = plugin.Manifest?.Author ?? "",
                ["toolCount"] = plugin.ToolNames.Count.ToString(),
                ["componentCount"] = plugin.ComponentNames.Count.ToString(),
                ["providerCount"] = string.Join(", ", plugin.ProviderIds),
            }
        });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });

    private PluginEntry? FindPlugin(string name)
    {
        return _engine.PluginLoader.LoadedPlugins
            .FirstOrDefault(p => p.PluginId.Equals(name, StringComparison.OrdinalIgnoreCase)
                             || Path.GetFileNameWithoutExtension(p.FileName).Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

// ================ 插件工具列表数据源 ================

internal class PluginToolsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public PluginToolsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var name = query?.RouteParams?.GetValueOrDefault("name") ?? "";
        var plugin = FindPlugin(name);
        if (plugin == null)
            return Task.FromResult(new DataResult { Data = new JsonArray() });

        // 获取该插件的所有工具（直接注册的 + 通过组件注册的）
        var toolNames = new HashSet<string>(plugin.ToolNames);
        var toolMap = new Dictionary<string, ITool>();

        // 从 ToolRegistry 获取直接注册的工具
        foreach (var tn in plugin.ToolNames)
        {
            var t = ToolRegistry.Get(tn);
            if (t != null) toolMap[tn] = t;
        }

        // 从组件实例获取组件工具
        foreach (var cn in plugin.ComponentNames)
        {
            foreach (var inst in _engine.GlobalComponentHost?.Instances ?? Enumerable.Empty<GlobalComponentInstance>())
            {
                if (inst.Component.Meta.Name != cn) continue;
                foreach (var tool in inst.Component.Tools)
                {
                    toolNames.Add(tool.Name);
                    toolMap[tool.Name] = tool;
                }
            }
            foreach (var inst in _engine.GetActiveEnginesSnapshot()
                .Select(e => e.ComponentHost).Where(h => h != null))
            {
                foreach (var loopInst in inst!.Instances)
                {
                    if (loopInst.Component.Meta.Name != cn) continue;
                    foreach (var tool in loopInst.Component.Tools)
                    {
                        toolNames.Add(tool.Name);
                        toolMap[tool.Name] = tool;
                    }
                }
            }
        }

        var arr = new JsonArray();
        foreach (var toolName in toolNames.OrderBy(n => n))
        {
            if (!toolMap.TryGetValue(toolName, out var tool))
            {
                tool = ToolRegistry.Get(toolName);
                if (tool == null) continue;
            }

            var meta = ToolRegistry.GetMeta(toolName)
                ?? Attribute.GetCustomAttribute(tool.GetType(), typeof(ToolMetaAttribute)) as ToolMetaAttribute;
            var isDisabled = ToolRegistry.IsDisabled(toolName);
            var reason = ToolRegistry.GetDisableReason(toolName);

            arr.Add(new JsonObject
            {
                ["name"] = toolName,
                ["description"] = tool.Description.Length > 60 ? tool.Description[..60] + "..." : tool.Description,
                ["group"] = meta?.Group ?? "—",
                ["disabled"] = isDisabled ? $"已禁用: {reason}" : "启用",
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var toolName = GetRowId(data);
        if (string.IsNullOrEmpty(toolName))
            return Task.FromResult(new ActionResult { Success = false, Message = "未指定工具" });

        switch (action)
        {
            case "disable":
                ToolRegistry.DisableTool(toolName, "管理员手动禁用");
                return Task.FromResult(new ActionResult { Success = true, Message = $"已禁用 {toolName}" });
            case "enable":
                ToolRegistry.EnableTool(toolName);
                return Task.FromResult(new ActionResult { Success = true, Message = $"已启用 {toolName}" });
            default:
                return Task.FromResult(new ActionResult { Success = false, Message = "未知操作" });
        }
    }

    /// <summary>行操作目标：优先 _row_id，兼容旧前端的 name/id。</summary>
    internal static string GetRowId(JsonNode? data)
    {
        if (data is not JsonObject obj) return "";
        return obj["_row_id"]?.ToString()
            ?? obj["id"]?.ToString()
            ?? obj["name"]?.ToString()
            ?? "";
    }

    private PluginEntry? FindPlugin(string name)
    {
        return _engine.PluginLoader.LoadedPlugins
            .FirstOrDefault(p => p.PluginId.Equals(name, StringComparison.OrdinalIgnoreCase)
                             || Path.GetFileNameWithoutExtension(p.FileName).Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

// ================ 插件组件列表数据源 ================

internal class PluginComponentsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public PluginComponentsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var name = query?.RouteParams?.GetValueOrDefault("name") ?? "";
        var plugin = FindPlugin(name);
        if (plugin == null)
            return Task.FromResult(new DataResult { Data = new JsonArray() });

        var arr = new JsonArray();
        foreach (var compName in plugin.ComponentNames)
        {
            var reg = ComponentRegistry.Get(compName);
            if (reg == null) continue;

            arr.Add(new JsonObject
            {
                ["componentName"] = compName,
                ["scope"] = reg.Scope.ToString(),
                ["channel"] = SessionTypeUiHelpers.IsLoadedInType(compName, "channel") ? "装载" : "—",
                ["task"] = SessionTypeUiHelpers.IsLoadedInType(compName, "task") ? "装载" : "—",
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });

    private PluginEntry? FindPlugin(string name)
    {
        return _engine.PluginLoader.LoadedPlugins
            .FirstOrDefault(p => p.PluginId.Equals(name, StringComparison.OrdinalIgnoreCase)
                             || Path.GetFileNameWithoutExtension(p.FileName).Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

// ================ 全部工具数据源 ================

internal class AllToolsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public AllToolsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        // Core/MCP 工具 + 组件工具
        var allTools = ToolRegistry.All.Values
            .Concat(_engine.GetAllComponentTools())
            .GroupBy(t => t.Name)
            .Select(g => g.First())
            .ToList();

        // 建立工具名→来源插件的映射
        var sourceMap = BuildSourceMap();

        // 筛选
        var filtered = allTools.AsEnumerable();

        // 来源筛选
        var sourceFilter = query?.Filters?.FirstOrDefault(f => f.Field == "source")?.Value;
        if (!string.IsNullOrEmpty(sourceFilter))
        {
            if (sourceFilter == "__core__")
                filtered = filtered.Where(t => !sourceMap.ContainsKey(t.Name));
            else
                filtered = filtered.Where(t => sourceMap.TryGetValue(t.Name, out var s) && s == sourceFilter);
        }

        // 分组筛选
        var groupFilter = query?.Filters?.FirstOrDefault(f => f.Field == "group")?.Value;
        if (!string.IsNullOrEmpty(groupFilter))
        {
            filtered = filtered.Where(t =>
            {
                var g = ToolRegistry.GetMeta(t.Name)?.Group;
                return g == groupFilter;
            });
        }

        // 搜索
        var search = query?.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var kw = search.ToLowerInvariant();
            filtered = filtered.Where(t =>
                t.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();

        // 注意：这里不做服务器端分页，因为 TableCard 使用客户端分页模式
        // 返回所有数据，让前端自行分页显示
        var arr = new JsonArray();
        foreach (var tool in list)
        {
            var meta = ToolRegistry.GetMeta(tool.Name)
                ?? Attribute.GetCustomAttribute(tool.GetType(), typeof(ToolMetaAttribute)) as ToolMetaAttribute;
            var isDisabled = ToolRegistry.IsDisabled(tool.Name);
            var reason = ToolRegistry.GetDisableReason(tool.Name);

            arr.Add(new JsonObject
            {
                ["id"] = tool.Name,
                ["name"] = tool.Name,
                ["description"] = tool.Description.Length > 80 ? tool.Description[..80] + "..." : tool.Description,
                ["source"] = sourceMap.TryGetValue(tool.Name, out var s) ? s : "核心",
                ["group"] = meta?.Group ?? "—",
                ["disabled"] = isDisabled ? $"已禁用: {reason}" : "启用",
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var toolName = data?["_row_id"]?.ToString() ?? data?["name"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(toolName))
            return Task.FromResult(new ActionResult { Success = false, Message = "未指定工具" });

        switch (action)
        {
            case "disable":
                ToolRegistry.DisableTool(toolName, "管理员手动禁用");
                return Task.FromResult(new ActionResult { Success = true, Message = $"已禁用 {toolName}" });
            case "enable":
                ToolRegistry.EnableTool(toolName);
                return Task.FromResult(new ActionResult { Success = true, Message = $"已启用 {toolName}" });
            default:
                return Task.FromResult(new ActionResult { Success = false, Message = "未知操作" });
        }
    }

    private Dictionary<string, string> BuildSourceMap()
    {
        var map = new Dictionary<string, string>();
        foreach (var plugin in _engine.PluginLoader.LoadedPlugins)
        {
            var pluginName = plugin.PluginId;

            // 直接注册的工具
            foreach (var tn in plugin.ToolNames)
                map[tn] = pluginName;

            // 通过组件注册的工具（直接从组件实例获取）
            foreach (var cn in plugin.ComponentNames)
            {
                var reg = ComponentRegistry.Get(cn);
                if (reg == null) continue;
                // Global 组件
                foreach (var inst in _engine.GlobalComponentHost?.Instances ?? Enumerable.Empty<GlobalComponentInstance>())
                {
                    if (inst.Component.Meta.Name != cn) continue;
                    foreach (var tool in inst.Component.Tools)
                        map[tool.Name] = pluginName;
                }
                // Loop 组件（遍历活跃引擎）
                foreach (var inst in _engine.GetActiveEnginesSnapshot()
                    .Select(e => e.ComponentHost).Where(h => h != null))
                {
                    foreach (var loopInst in inst!.Instances)
                    {
                        if (loopInst.Component.Meta.Name != cn) continue;
                        foreach (var tool in loopInst.Component.Tools)
                            map[tool.Name] = pluginName;
                    }
                }
            }
        }
        return map;
    }
}


// ================ 会话类型数据源 ================

internal static class SessionTypeUiHelpers
{
    public static string ModeLabel(string mode)
        => string.Equals(mode, "blacklist", StringComparison.OrdinalIgnoreCase) ? "黑名单" : "白名单";

    public static string ModeDesc(string mode)
        => string.Equals(mode, "blacklist", StringComparison.OrdinalIgnoreCase)
            ? "黑名单：清单中的工具被禁用" : "白名单：仅允许清单中的工具";

    /// <summary>组件是否在指定会话类型的装配清单中（Global 组件恒装载，不参与类型清单）。</summary>
    public static bool IsLoadedInType(string componentName, string type)
        => SessionTypeProfiles.Get(type).Components.Contains(componentName, StringComparer.OrdinalIgnoreCase);

    /// <summary>工具名 → 来源插件映射（核心工具无映射）。</summary>
    public static Dictionary<string, string> BuildSourceMap(MasterEngine engine)
    {
        var map = new Dictionary<string, string>();
        foreach (var plugin in engine.PluginLoader.LoadedPlugins)
        {
            var pluginName = plugin.PluginId;
            foreach (var tn in plugin.ToolNames)
                map[tn] = pluginName;
            foreach (var cn in plugin.ComponentNames)
            {
                var reg = ComponentRegistry.Get(cn);
                if (reg == null) continue;
                foreach (var inst in engine.GlobalComponentHost?.Instances ?? Enumerable.Empty<GlobalComponentInstance>())
                {
                    if (inst.Component.Meta.Name != cn) continue;
                    foreach (var tool in inst.Component.Tools)
                        map[tool.Name] = pluginName;
                }
                foreach (var inst in engine.GetActiveEnginesSnapshot()
                    .Select(e => e.ComponentHost).Where(h => h != null))
                {
                    foreach (var loopInst in inst!.Instances)
                    {
                        if (loopInst.Component.Meta.Name != cn) continue;
                        foreach (var tool in loopInst.Component.Tools)
                            map[tool.Name] = pluginName;
                    }
                }
            }
        }
        return map;
    }
}

internal class SessionTypesListSource : IDataSource
{
    public SessionTypesListSource() { }
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var arr = new JsonArray();
        foreach (var t in SessionTypeProfiles.All())
        {
            var p = SessionTypeProfiles.Get(t);
            arr.Add(new JsonObject
            {
                ["id"] = t,
                ["type"] = t,
                ["mode"] = SessionTypeUiHelpers.ModeLabel(p.Tools.Mode),
                ["toolCount"] = p.Tools.List.Count,
                ["componentCount"] = p.Components.Length,
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });
}

internal class SessionTypeDetailSource : IDataSource
{
    private string _selectedType = "";

    public SessionTypeDetailSource() { }
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        if (query?.Extra is JsonObject extra)
        {
            var id = extra["id"]?.ToString();
            if (!string.IsNullOrEmpty(id))
                _selectedType = id;
        }

        if (string.IsNullOrEmpty(_selectedType))
            return Task.FromResult(new DataResult
            {
                Data = new JsonObject
                {
                    ["type"] = "选择左侧会话类型",
                    ["core"] = "—", ["mode"] = "—", ["modeDesc"] = "—",
                    ["toolCount"] = "—", ["componentCount"] = "—",
                }
            });

        var p = SessionTypeProfiles.Get(_selectedType);
        return Task.FromResult(new DataResult
        {
            Data = new JsonObject
            {
                ["type"] = _selectedType,
                ["core"] = p.DefaultCoreConfig,
                ["mode"] = SessionTypeUiHelpers.ModeLabel(p.Tools.Mode),
                ["modeDesc"] = SessionTypeUiHelpers.ModeDesc(p.Tools.Mode),
                ["toolCount"] = p.Tools.List.Count.ToString(),
                ["componentCount"] = p.Components.Length.ToString(),
            }
        });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_selectedType))
            return Task.FromResult(new ActionResult { Success = false, Message = "未选择会话类型" });

        switch (action)
        {
            case "toggle-mode":
            {
                var mode = SessionTypeProfiles.GetMode(_selectedType);
                var flipped = string.Equals(mode, "blacklist", StringComparison.OrdinalIgnoreCase) ? "whitelist" : "blacklist";
                SessionTypeProfiles.Save(_selectedType, flipped, SessionTypeProfiles.GetTools(_selectedType));
                return Task.FromResult(new ActionResult { Success = true, Message = $"{_selectedType} → {SessionTypeUiHelpers.ModeLabel(flipped)}（已保存）" });
            }
            case "reset":
            {
                // 恢复默认应尽量完整；SessionTypeProfiles.GetDefault 当前只提供 Tools 默认。
                // 组件清单没有默认种子，保留现状并在消息中说明，避免给出虚假的“全部已重置”。
                var (dMode, dTools) = SessionTypeProfiles.GetDefault(_selectedType);
                SessionTypeProfiles.Save(_selectedType, dMode, dTools);
                return Task.FromResult(new ActionResult
                {
                    Success = true,
                    Message = $"{_selectedType} 工具清单/模式已恢复默认（{SessionTypeUiHelpers.ModeLabel(dMode)}）；组件清单保持现状"
                });
            }
            default:
                return Task.FromResult(new ActionResult { Success = false, Message = $"未知操作: {action}" });
        }
    }
}

internal class SessionTypeComponentsSource : IDataSource
{
    private readonly MasterEngine _engine;
    private string _selectedType = "";

    public SessionTypeComponentsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        if (query?.Extra is JsonObject extra)
        {
            var id = extra["id"]?.ToString();
            if (!string.IsNullOrEmpty(id))
                _selectedType = id;
        }

        if (string.IsNullOrEmpty(_selectedType))
            return Task.FromResult(new DataResult { Data = new JsonArray(), TotalCount = 0 });

        var inList = new HashSet<string>(SessionTypeProfiles.Get(_selectedType).Components, StringComparer.OrdinalIgnoreCase);
        var arr = new JsonArray();
        foreach (var reg in ComponentRegistry.GetAll().Where(r => r.Scope == ComponentScope.Loop))
        {
            var name = ComponentAttribute.GetFrom(reg.Type)?.Name ?? reg.Type.Name;
            arr.Add(new JsonObject
            {
                ["id"] = name,
                ["name"] = name,
                ["scope"] = "Loop",
                ["inList"] = inList.Contains(name) ? "✓" : "—",
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var compName = data?["_row_id"]?.ToString() ?? data?["name"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(compName))
            return Task.FromResult(new ActionResult { Success = false, Message = "未指定组件" });
        if (string.IsNullOrEmpty(_selectedType))
            return Task.FromResult(new ActionResult { Success = false, Message = "未选择会话类型" });

        var list = SessionTypeProfiles.Get(_selectedType).Components.ToList();
        switch (action)
        {
            case "add":
                if (list.Contains(compName, StringComparer.OrdinalIgnoreCase))
                    return Task.FromResult(new ActionResult { Success = false, Message = $"「{compName}」已在 {_selectedType} 清单中" });
                list.Add(compName);
                break;
            case "remove":
                if (!list.RemoveAll(c => c.Equals(compName, StringComparison.OrdinalIgnoreCase)).Equals(0))
                    break;
                return Task.FromResult(new ActionResult { Success = false, Message = $"「{compName}」不在 {_selectedType} 清单中" });
            default:
                return Task.FromResult(new ActionResult { Success = false, Message = $"未知操作: {action}" });
        }

        SessionTypeProfiles.SaveComponents(_selectedType, list);
        return Task.FromResult(new ActionResult { Success = true, Message = $"{compName} × {_selectedType} 组件清单已更新（新会话生效）" });
    }
}

internal class SessionTypeToolsSource : IDataSource
{
    private readonly MasterEngine _engine;
    private string _selectedType = "";

    public SessionTypeToolsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        if (query?.Extra is JsonObject extra)
        {
            var id = extra["id"]?.ToString();
            if (!string.IsNullOrEmpty(id))
                _selectedType = id;
        }

        if (string.IsNullOrEmpty(_selectedType))
            return Task.FromResult(new DataResult { Data = new JsonArray(), TotalCount = 0 });

        var inList = new HashSet<string>(SessionTypeProfiles.GetTools(_selectedType), StringComparer.OrdinalIgnoreCase);

        // 全部工具（核心 + 组件 + 插件）
        var allTools = ToolRegistry.All.Values
            .Concat(_engine.GetAllComponentTools())
            .GroupBy(t => t.Name)
            .Select(g => g.First())
            .ToList();

        var sourceMap = SessionTypeUiHelpers.BuildSourceMap(_engine);

        var filtered = allTools.AsEnumerable();

        var sourceFilter = query?.Filters?.FirstOrDefault(f => f.Field == "source")?.Value;
        if (!string.IsNullOrEmpty(sourceFilter))
        {
            if (sourceFilter == "__core__")
                filtered = filtered.Where(t => !sourceMap.ContainsKey(t.Name));
            else
                filtered = filtered.Where(t => sourceMap.TryGetValue(t.Name, out var s) && s == sourceFilter);
        }

        var search = query?.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var kw = search.ToLowerInvariant();
            filtered = filtered.Where(t =>
                t.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        var arr = new JsonArray();
        foreach (var tool in filtered.OrderBy(t => t.Name))
        {
            var inIt = inList.Contains(tool.Name);
            arr.Add(new JsonObject
            {
                ["id"] = tool.Name,
                ["name"] = tool.Name,
                ["description"] = tool.Description.Length > 80 ? tool.Description[..80] + "..." : tool.Description,
                ["source"] = sourceMap.TryGetValue(tool.Name, out var s) ? s : "核心",
                ["inList"] = inIt ? "✓" : "—",
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var toolName = data?["_row_id"]?.ToString() ?? data?["name"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(toolName))
            return Task.FromResult(new ActionResult { Success = false, Message = "未指定工具" });
        if (string.IsNullOrEmpty(_selectedType))
            return Task.FromResult(new ActionResult { Success = false, Message = "未选择会话类型" });

        var tools = SessionTypeProfiles.GetTools(_selectedType).ToList();
        var mode = SessionTypeProfiles.GetMode(_selectedType);

        switch (action)
        {
            case "add":
                if (tools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
                    return Task.FromResult(new ActionResult { Success = false, Message = $"「{toolName}」已在清单中" });
                tools.Add(toolName);
                break;
            case "remove":
                if (tools.RemoveAll(t => t.Equals(toolName, StringComparison.OrdinalIgnoreCase)) == 0)
                    return Task.FromResult(new ActionResult { Success = false, Message = $"「{toolName}」不在清单中" });
                break;
            default:
                return Task.FromResult(new ActionResult { Success = false, Message = $"未知操作: {action}" });
        }

        SessionTypeProfiles.Save(_selectedType, mode, tools);
        return Task.FromResult(new ActionResult { Success = true, Message = $"{toolName} × {_selectedType} 已更新（运行中会话即时生效）" });
    }
}
