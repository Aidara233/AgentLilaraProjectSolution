using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Engine;
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
        BuildComponentPermsPage(),
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
                Layout = new CardLayout { PreferredCols = 4 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "plugin-list", Source = new PluginListSource(_engine) },
            new() { Id = "plugin-reload", Source = new PluginReloadSource(_engine) },
        }
    };

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
                        new() { Field = "fileName", Label = "文件名" },
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
                        new() { Field = "system", Header = "系统", Width = "60px" },
                    },
                    Searchable = false, Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "plugin-info", Source = new PluginInfoSource(_engine) },
            new() { Id = "plugin-reload-single", Source = new PluginReloadSingleSource(_engine) },
            new() { Id = "plugin-tools", Source = new PluginToolsSource(_engine) },
            new() { Id = "plugin-components", Source = new PluginComponentsSource(_engine) },
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
                        new() { Field = "express", Header = "Express", Width = "70px" },
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
                    Searchable = true, Paginated = true, DefaultPageSize = 30
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "all-tools", Source = new AllToolsSource(_engine) },
        }
    };

    // ================ 4. 组件权限 ================

    private PageDefinition BuildComponentPermsPage() => new()
    {
        Route = "plugins/permissions",
        Meta = new PageMeta { Title = "组件权限", Icon = "bi-sliders2", Group = "插件", Order = 92 },
        LayoutType = PageLayoutType.Sidebar,
        Cards = new List<CardDefinition>
        {
            // 左侧：组件列表
            new()
            {
                Id = "perm-list", Type = CardType.Table, DataSourceId = "perm-list", Title = "组件",
                LinkEvent = "perm-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "componentName", Header = "组件名" },
                        new() { Field = "scope", Header = "作用域", Width = "70px", Format = ColumnFormat.Badge },
                    },
                    Searchable = false, Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 3, GridColumnStart = 1 }
            },
            // 右侧：引擎类型开关
            new()
            {
                Id = "perm-toggles", Type = CardType.Status, DataSourceId = "perm-toggles",
                Title = "引擎类型权限", ListenEvent = "perm-selected",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "componentName", Label = "组件" },
                        new() { Field = "channel", Label = "Channel", Type = StatusFieldType.Badge },
                        new() { Field = "system", Label = "System", Type = StatusFieldType.Badge },
                        new() { Field = "subAgent", Label = "Sub-Agent", Type = StatusFieldType.Badge },
                        new() { Field = "review", Label = "Review", Type = StatusFieldType.Badge },
                    },
                    Actions = new()
                    {
                        new() { Id = "toggle-channel", Label = "切换 Channel" },
                        new() { Id = "toggle-system", Label = "切换 System" },
                        new() { Id = "toggle-subAgent", Label = "切换 Sub-Agent" },
                        new() { Id = "toggle-review", Label = "切换 Review" },
                    }
                },
                Layout = new CardLayout { PreferredCols = 9, GridColumnStart = 4 }
            },
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "perm-list", Source = new ComponentPermListSource(_engine) },
            new() { Id = "perm-toggles", Source = new ComponentPermTogglesSource(_engine) },
        }
    };

    // ---- 筛选选项构建 ----

    private List<SelectOption> BuildSourceFilterOptions()
    {
        var options = new List<SelectOption> { new() { Value = "__core__", Label = "核心工具" } };
        foreach (var plugin in _engine.PluginLoader.LoadedPlugins)
        {
            var name = Path.GetFileNameWithoutExtension(plugin.FileName);
            options.Add(new SelectOption { Value = name, Label = name });
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
        // 组件工具也检查 metadata
        foreach (var tool in _engine.GetAllComponentTools())
        {
            var meta = ToolRegistry.GetMeta(tool.Name)
                ?? Attribute.GetCustomAttribute(tool.GetType(), typeof(ToolMetaAttribute)) as ToolMetaAttribute;
            if (!string.IsNullOrEmpty(meta?.Group))
                groups.Add(meta.Group);
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
            var name = Path.GetFileNameWithoutExtension(plugin.FileName);
            arr.Add(new JsonObject
            {
                ["id"] = name,
                ["fileName"] = plugin.FileName,
                ["toolCount"] = plugin.ToolNames.Count,
                ["componentCount"] = plugin.ComponentNames.Count,
                ["providerCount"] = plugin.ProviderIds.Count,
                ["injectCount"] = plugin.InjectProviderNames.Count,
                ["lifecycleCount"] = plugin.LifecycleNames.Count,
                ["_link"] = $"/p/plugins/{name}",
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
                ["fileName"] = plugin.FileName,
                ["filePath"] = plugin.FilePath,
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
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p.FileName) == name);
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
        var toolName = data?["_row_id"]?.ToString() ?? "";
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

    private PluginEntry? FindPlugin(string name)
    {
        return _engine.PluginLoader.LoadedPlugins
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p.FileName) == name);
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
                ["channel"] = reg.ChannelApplicability.ToString(),
                ["system"] = reg.SystemApplicability.ToString(),
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });

    private PluginEntry? FindPlugin(string name)
    {
        return _engine.PluginLoader.LoadedPlugins
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p.FileName) == name);
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
        var total = list.Count;

        // 分页
        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 30;
        var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var arr = new JsonArray();
        foreach (var tool in paged)
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
                ["express"] = meta?.ExpressAvailable == true ? "是" : "—",
                ["disabled"] = isDisabled ? $"已禁用: {reason}" : "启用",
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = total });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var toolName = data?["_row_id"]?.ToString() ?? "";
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
            var pluginName = Path.GetFileNameWithoutExtension(plugin.FileName);

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

// ================ 组件权限数据源 ================

internal class ComponentPermListSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ComponentPermListSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var config = ComponentConfig.Load();
        var registrations = ComponentRegistry.GetAll();

        var arr = new JsonArray();
        foreach (var reg in registrations)
        {
            var attr = ComponentAttribute.GetFrom(reg.Type);
            var name = attr?.Name ?? reg.Type.Name;
            arr.Add(new JsonObject
            {
                ["id"] = name,
                ["componentName"] = name,
                ["scope"] = reg.Scope == ComponentScope.Global ? "Global" : "Loop",
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });
}

internal class ComponentPermTogglesSource : IDataSource
{
    private readonly MasterEngine _engine;
    private string _selectedComponent = "";

    public ComponentPermTogglesSource(MasterEngine engine) => _engine = engine;

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        if (query?.Extra is JsonObject extra)
        {
            var id = extra["id"]?.ToString();
            if (!string.IsNullOrEmpty(id))
                _selectedComponent = id;
        }

        if (string.IsNullOrEmpty(_selectedComponent))
            return Task.FromResult(new DataResult
            {
                Data = new JsonObject
                {
                    ["componentName"] = "选择左侧组件",
                    ["channel"] = "—", ["system"] = "—",
                    ["subAgent"] = "—", ["review"] = "—",
                }
            });

        var config = ComponentConfig.Load();
        var reg = ComponentRegistry.Get(_selectedComponent);
        var data = new JsonObject
        {
            ["componentName"] = _selectedComponent,
            ["channel"] = config.IsEnabled(_selectedComponent, "channel", true, reg?.ChannelApplicability ?? Applicability.Enabled) ? "启用" : "禁用",
            ["system"] = config.IsEnabled(_selectedComponent, "system", true, reg?.SystemApplicability ?? Applicability.Enabled) ? "启用" : "禁用",
            ["subAgent"] = config.IsEnabled(_selectedComponent, "sub-agent", true, reg?.SubAgentApplicability ?? Applicability.Enabled) ? "启用" : "禁用",
            ["review"] = config.IsEnabled(_selectedComponent, "review", true, reg?.ReviewApplicability ?? Applicability.Enabled) ? "启用" : "禁用",
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_selectedComponent))
            return new ActionResult { Success = false, Message = "未选择组件" };

        var engineType = action switch
        {
            "toggle-channel" => "channel",
            "toggle-system" => "system",
            "toggle-subAgent" => "sub-agent",
            "toggle-review" => "review",
            _ => null
        };

        if (engineType == null)
            return new ActionResult { Success = false, Message = $"未知操作: {action}" };

        var config = ComponentConfig.Load();
        var reg = ComponentRegistry.Get(_selectedComponent);
        var applicability = engineType switch
        {
            "channel" => reg?.ChannelApplicability ?? Applicability.Enabled,
            "system" => reg?.SystemApplicability ?? Applicability.Enabled,
            "sub-agent" => reg?.SubAgentApplicability ?? Applicability.Enabled,
            "review" => reg?.ReviewApplicability ?? Applicability.Enabled,
            _ => Applicability.Enabled
        };
        var current = config.IsEnabled(_selectedComponent, engineType, true, applicability);
        var target = !current;

        try
        {
            await _engine.ToggleComponentLiveAsync(_selectedComponent, engineType, target);
            var label = target ? "启用" : "禁用";
            return new ActionResult { Success = true, Message = $"{_selectedComponent} × {engineType} → {label}（已即时生效）" };
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Message = $"切换失败: {ex.Message}" };
        }
    }
}
