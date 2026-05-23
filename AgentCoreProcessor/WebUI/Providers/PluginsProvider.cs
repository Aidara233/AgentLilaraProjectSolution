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
        BuildProfilesPage(),
    };

    // ================ 1. 插件清单 ================

    private PageDefinition BuildPluginListPage() => new()
    {
        Route = "plugins",
        Meta = new PageMeta { Title = "插件清单", Icon = "bi-puzzle", Group = "插件", Order = 10 },
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
                Id = "plugin-tools", Type = CardType.Table, DataSourceId = "plugin-tools", Title = "工具列表",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "name", Header = "工具名" },
                        new() { Field = "description", Header = "描述" },
                        new() { Field = "group", Header = "分组", Width = "100px" },
                        new() { Field = "permission", Header = "权限", Width = "80px" },
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
            new() { Id = "plugin-tools", Source = new PluginToolsSource(_engine) },
            new() { Id = "plugin-components", Source = new PluginComponentsSource(_engine) },
        }
    };

    // ================ 3. 工具列表 ================

    private PageDefinition BuildToolsPage() => new()
    {
        Route = "plugins/tools",
        Meta = new PageMeta { Title = "工具列表", Icon = "bi-tools", Group = "插件", Order = 30 },
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
                        new() { Field = "permission", Header = "权限", Width = "70px" },
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

    // ================ 4. Profile 配置 ================

    private PageDefinition BuildProfilesPage() => new()
    {
        Route = "plugins/profiles",
        Meta = new PageMeta { Title = "引擎配置", Icon = "bi-sliders2", Group = "插件", Order = 40 },
        LayoutType = PageLayoutType.Sidebar,
        Cards = new List<CardDefinition>
        {
            // 左侧：Profile 列表
            new()
            {
                Id = "profile-list", Type = CardType.Table, DataSourceId = "profile-list", Title = "Profile",
                LinkEvent = "profile-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "name", Header = "名称" },
                        new() { Field = "inherits", Header = "继承自", Width = "80px" },
                        new() { Field = "isDefault", Header = "默认", Width = "60px", Sortable = false },
                    },
                    Searchable = false, Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 3, GridColumnStart = 1 }
            },
            // 左侧：操作
            new()
            {
                Id = "profile-actions", Type = CardType.Action, DataSourceId = "profile-actions", Title = "操作",
                Schema = new ActionCardSchema
                {
                    ActionId = "profile-op",
                    ActionLabel = "执行操作",
                    SubmitLabel = "执行",
                    Params = new()
                    {
                        new() { Name = "action", Label = "操作", Type = "select", Required = true,
                            Options = new()
                            {
                                new() { Value = "create", Label = "新建 Profile" },
                                new() { Value = "set-default", Label = "设为默认" },
                                new() { Value = "delete", Label = "删除 Profile" },
                                new() { Value = "reset", Label = "重置为默认" },
                            } },
                        new() { Name = "name", Label = "Profile 名称", Required = false },
                        new() { Name = "parent", Label = "继承自", Required = false },
                        new() { Name = "description", Label = "描述", Required = false },
                    },
                    Description = "新建/删除/重置 Profile。新建需填名称和继承父级。"
                },
                Layout = new CardLayout { PreferredCols = 3, GridColumnStart = 1, Order = 1 }
            },
            // 右侧：详情
            new()
            {
                Id = "profile-info", Type = CardType.Status, DataSourceId = "profile-info", Title = "Profile 详情",
                ListenEvent = "profile-selected",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "name", Label = "名称" },
                        new() { Field = "inherits", Label = "继承自" },
                        new() { Field = "description", Label = "描述" },
                        new() { Field = "channelCount", Label = "使用频道数" },
                        new() { Field = "blockedCount", Label = "屏蔽工具数" },
                    }
                },
                Layout = new CardLayout { PreferredCols = 9, GridColumnStart = 4 }
            },
            // 右侧：组件状态
            new()
            {
                Id = "profile-components", Type = CardType.Table, DataSourceId = "profile-components",
                Title = "组件状态", ListenEvent = "profile-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "componentName", Header = "组件" },
                        new() { Field = "state", Header = "状态", Width = "100px" },
                        new() { Field = "source", Header = "来源", Width = "80px", Sortable = false },
                    },
                    RowActions = new()
                    {
                        new() { Id = "set-enabled", Label = "启用" },
                        new() { Id = "set-disabled", Label = "可激活" },
                        new() { Id = "set-unavailable", Label = "不可用" },
                        new() { Id = "inherit", Label = "恢复继承" },
                    },
                    Searchable = false, Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 9, GridColumnStart = 4 }
            },
            // 右侧：工具屏蔽
            new()
            {
                Id = "profile-blocked", Type = CardType.Table, DataSourceId = "profile-blocked",
                Title = "工具屏蔽", ListenEvent = "profile-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "toolName", Header = "工具名" },
                        new() { Field = "type", Header = "类型", Width = "80px", Sortable = false },
                    },
                    RowActions = new()
                    {
                        new() { Id = "remove-block", Label = "移除", Danger = true },
                    },
                    Searchable = false, Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 5, GridColumnStart = 4 }
            },
            // 右侧：添加工具屏蔽
            new()
            {
                Id = "profile-add-block", Type = CardType.Action, DataSourceId = "profile-add-block",
                Title = "添加工具屏蔽", ListenEvent = "profile-selected",
                Schema = new ActionCardSchema
                {
                    ActionId = "add-block",
                    ActionLabel = "添加屏蔽/解除",
                    SubmitLabel = "添加",
                    Params = new()
                    {
                        new() { Name = "toolName", Label = "工具名", Required = true },
                        new() { Name = "mode", Label = "模式", Type = "select", Required = true,
                            Options = new()
                            {
                                new() { Value = "block", Label = "屏蔽" },
                                new() { Value = "unblock", Label = "解除屏蔽" },
                            } }
                    }
                },
                Layout = new CardLayout { PreferredCols = 4, GridColumnStart = 4 }
            },
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "profile-list", Source = new ProfileListSource(_engine) },
            new() { Id = "profile-actions", Source = new ProfileActionsSource(_engine) },
            new() { Id = "profile-info", Source = new ProfileInfoSource(_engine) },
            new() { Id = "profile-components", Source = new ProfileComponentsSource(_engine) },
            new() { Id = "profile-blocked", Source = new ProfileBlockedSource(_engine) },
            new() { Id = "profile-add-block", Source = new ProfileAddBlockSource(_engine) },
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
        if (action != "reload")
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

        // 获取该插件的所有工具（通过组件注册的 + 直接注册的）
        var toolNames = new HashSet<string>(plugin.ToolNames);

        // 也查找通过组件注册的工具（同 assembly）
        foreach (var reg in ComponentRegistry.GetAll())
        {
            var compAttr = ComponentAttribute.GetFrom(reg.Type);
            if (compAttr != null && plugin.ComponentNames.Contains(compAttr.Name))
            {
                foreach (var tool in ToolRegistry.All.Values)
                {
                    if (tool.GetType().Assembly == reg.SourceAssembly)
                        toolNames.Add(tool.Name);
                }
            }
        }

        var arr = new JsonArray();
        foreach (var toolName in toolNames.OrderBy(n => n))
        {
            var tool = ToolRegistry.Get(toolName);
            if (tool == null) continue;

            var meta = ToolRegistry.GetMeta(toolName);
            var isDisabled = ToolRegistry.IsDisabled(toolName);
            var reason = ToolRegistry.GetDisableReason(toolName);

            arr.Add(new JsonObject
            {
                ["name"] = toolName,
                ["description"] = tool.Description.Length > 60 ? tool.Description[..60] + "..." : tool.Description,
                ["group"] = meta?.Group ?? "—",
                ["permission"] = meta?.Permission.ToString() ?? "Default",
                ["disabled"] = isDisabled ? $"已禁用: {reason}" : "启用",
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var toolName = data?["_row_id"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(toolName))
            return new ActionResult { Success = false, Message = "未指定工具" };

        switch (action)
        {
            case "disable":
                ToolRegistry.DisableTool(toolName, "管理员手动禁用");
                return new ActionResult { Success = true, Message = $"已禁用 {toolName}" };
            case "enable":
                ToolRegistry.EnableTool(toolName);
                return new ActionResult { Success = true, Message = $"已启用 {toolName}" };
            default:
                return new ActionResult { Success = false, Message = "未知操作" };
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
        var allTools = ToolRegistry.All.Values.ToList();

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
            var meta = ToolRegistry.GetMeta(tool.Name);
            var isDisabled = ToolRegistry.IsDisabled(tool.Name);
            var reason = ToolRegistry.GetDisableReason(tool.Name);

            arr.Add(new JsonObject
            {
                ["id"] = tool.Name,
                ["name"] = tool.Name,
                ["description"] = tool.Description.Length > 80 ? tool.Description[..80] + "..." : tool.Description,
                ["source"] = sourceMap.TryGetValue(tool.Name, out var s) ? s : "核心",
                ["group"] = meta?.Group ?? "—",
                ["permission"] = meta?.Permission.ToString() ?? "Default",
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

            // 通过组件注册的工具（同 assembly）
            foreach (var cn in plugin.ComponentNames)
            {
                var reg = ComponentRegistry.Get(cn);
                if (reg == null) continue;
                var compAttr = ComponentAttribute.GetFrom(reg.Type);
                if (compAttr == null) continue;
                foreach (var tool in ToolRegistry.All.Values)
                {
                    if (tool.GetType().Assembly == reg.SourceAssembly)
                        map[tool.Name] = pluginName;
                }
            }
        }
        return map;
    }
}

// ================ Profile 数据源 ================

internal class ProfileListSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ProfileListSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var profiles = _engine.ToolProfiles;
        var defaultProfile = profiles.GetDefaultProfile();
        var names = profiles.GetProfileNames();

        var arr = new JsonArray();
        foreach (var name in names)
        {
            var p = profiles.GetProfile(name);
            arr.Add(new JsonObject
            {
                ["id"] = name,
                ["name"] = name,
                ["inherits"] = p?.Inherits ?? "—",
                ["isDefault"] = name == defaultProfile ? "★" : "",
                ["_link"] = "", // 留空，用 LinkEvent 而非路由跳转
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });
}

internal class ProfileActionsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ProfileActionsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
        => Task.FromResult(new DataResult { Data = new JsonObject() });

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var profiles = _engine.ToolProfiles;
        var names = profiles.GetProfileNames();
        var op = data?["action"]?.ToString() ?? "";

        switch (op)
        {
            case "create":
                var newName = data?["name"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(newName))
                    return Task.FromResult(new ActionResult { Success = false, Message = "名称不能为空" });
                if (names.Contains(newName))
                    return Task.FromResult(new ActionResult { Success = false, Message = $"Profile '{newName}' 已存在" });
                var parent = data?["parent"]?.ToString() ?? "_root";
                if (string.IsNullOrWhiteSpace(parent)) parent = "_root";
                var desc = data?["description"]?.ToString() ?? "";
                profiles.AddProfile(newName, new ToolProfile
                {
                    Inherits = parent,
                    Description = string.IsNullOrWhiteSpace(desc) ? null : desc
                });
                return Task.FromResult(new ActionResult { Success = true, Message = $"已创建 Profile '{newName}'" });

            case "delete":
                var delName = data?["name"]?.ToString();
                if (string.IsNullOrEmpty(delName) || delName == "_root")
                    return Task.FromResult(new ActionResult { Success = false, Message = "不能删除 _root" });
                profiles.RemoveProfile(delName);
                return Task.FromResult(new ActionResult { Success = true, Message = $"已删除 Profile '{delName}'" });

            case "set-default":
                var defName = data?["name"]?.ToString();
                if (string.IsNullOrEmpty(defName))
                    return Task.FromResult(new ActionResult { Success = false });
                profiles.SetDefaultProfile(defName);
                return Task.FromResult(new ActionResult { Success = true, Message = $"已将 '{defName}' 设为默认" });

            case "reset":
                profiles.ResetToDefault();
                return Task.FromResult(new ActionResult { Success = true, Message = "已重置为默认配置" });

            default:
                return Task.FromResult(new ActionResult { Success = false, Message = "未知操作" });
        }
    }
}

internal class ProfileInfoSource : IDataSource
{
    private readonly MasterEngine _engine;
    private string _selectedProfile = "_root";
    public ProfileInfoSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        // 从 LinkEvent 获取选中的 profile
        if (query?.Extra is JsonObject extra)
        {
            var id = extra["id"]?.ToString();
            if (!string.IsNullOrEmpty(id))
                _selectedProfile = id;
        }

        var profile = _engine.ToolProfiles.GetProfile(_selectedProfile);
        var channels = _engine.ToolProfiles.GetChannelMapping()
            .Where(kv => kv.Value == _selectedProfile).ToList();

        return Task.FromResult(new DataResult
        {
            Data = new JsonObject
            {
                ["name"] = _selectedProfile,
                ["inherits"] = profile?.Inherits ?? "—",
                ["description"] = profile?.Description ?? "",
                ["channelCount"] = channels.Count.ToString(),
                ["blockedCount"] = (profile?.BlockedTools.Count ?? 0) + (profile?.UnblockedTools.Count ?? 0).ToString(),
            }
        });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });
}

internal class ProfileComponentsSource : IDataSource
{
    private readonly MasterEngine _engine;
    private string _selectedProfile = "_root";
    public ProfileComponentsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        if (query?.Extra is JsonObject extra)
        {
            var id = extra["id"]?.ToString();
            if (!string.IsNullOrEmpty(id))
                _selectedProfile = id;
        }

        var profile = _engine.ToolProfiles.GetProfile(_selectedProfile);
        if (profile == null)
            return Task.FromResult(new DataResult { Data = new JsonArray() });

        // 收集所有已知组件
        var allComponents = new HashSet<string>();
        foreach (var name in _engine.ToolProfiles.GetProfileNames())
        {
            var p = _engine.ToolProfiles.GetProfile(name);
            if (p != null)
                foreach (var k in p.Components.Keys) allComponents.Add(k);
        }
        foreach (var reg in ComponentRegistry.GetAll())
        {
            var attr = AgentLilara.PluginSDK.ComponentAttribute.GetFrom(reg.Type);
            if (attr != null) allComponents.Add(attr.Name);
        }

        var arr = new JsonArray();
        foreach (var compName in allComponents.OrderBy(n => n))
        {
            var state = _engine.ToolProfiles.GetComponentState(_selectedProfile, compName);
            var isOwn = profile.Components.ContainsKey(compName);
            arr.Add(new JsonObject
            {
                ["id"] = compName,
                ["componentName"] = compName,
                ["state"] = state switch
                {
                    ComponentState.Enabled => "启用",
                    ComponentState.Disabled => "可激活",
                    _ => "不可用"
                },
                ["source"] = isOwn ? "本节点" : "继承",
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var compName = data?["_row_id"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(compName) || _selectedProfile == null)
            return Task.FromResult(new ActionResult { Success = false, Message = "未指定组件" });

        var profile = _engine.ToolProfiles.GetProfile(_selectedProfile);
        if (profile == null)
            return Task.FromResult(new ActionResult { Success = false });

        switch (action)
        {
            case "set-enabled":
                profile.Components[compName] = "enabled";
                break;
            case "set-disabled":
                profile.Components[compName] = "disabled";
                break;
            case "set-unavailable":
                profile.Components[compName] = "unavailable";
                break;
            case "inherit":
                profile.Components.Remove(compName);
                break;
            default:
                return Task.FromResult(new ActionResult { Success = false, Message = "未知操作" });
        }

        _engine.ToolProfiles.Save();
        return Task.FromResult(new ActionResult { Success = true, Message = $"组件 '{compName}' 状态已更新" });
    }
}

internal class ProfileBlockedSource : IDataSource
{
    private readonly MasterEngine _engine;
    private string _selectedProfile = "_root";
    public ProfileBlockedSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        if (query?.Extra is JsonObject extra)
        {
            var id = extra["id"]?.ToString();
            if (!string.IsNullOrEmpty(id))
                _selectedProfile = id;
        }

        var profile = _engine.ToolProfiles.GetProfile(_selectedProfile);
        if (profile == null)
            return Task.FromResult(new DataResult { Data = new JsonArray() });

        var arr = new JsonArray();
        foreach (var t in profile.BlockedTools)
        {
            arr.Add(new JsonObject
            {
                ["id"] = t,
                ["toolName"] = t,
                ["type"] = "屏蔽",
            });
        }
        foreach (var t in profile.UnblockedTools)
        {
            arr.Add(new JsonObject
            {
                ["id"] = t,
                ["toolName"] = t,
                ["type"] = "解除屏蔽",
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var toolName = data?["_row_id"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(toolName))
            return Task.FromResult(new ActionResult { Success = false });

        var profile = _engine.ToolProfiles.GetProfile(_selectedProfile);
        if (profile == null)
            return Task.FromResult(new ActionResult { Success = false });

        if (action == "remove-block")
        {
            profile.BlockedTools.Remove(toolName);
            profile.UnblockedTools.Remove(toolName);
            _engine.ToolProfiles.Save();
            return Task.FromResult(new ActionResult { Success = true, Message = $"已移除 '{toolName}'" });
        }

        return Task.FromResult(new ActionResult { Success = false, Message = "未知操作" });
    }
}

internal class ProfileAddBlockSource : IDataSource
{
    private readonly MasterEngine _engine;
    private string _selectedProfile = "_root";
    public ProfileAddBlockSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        if (query?.Extra is JsonObject extra)
        {
            var id = extra["id"]?.ToString();
            if (!string.IsNullOrEmpty(id))
                _selectedProfile = id;
        }
        return Task.FromResult(new DataResult { Data = new JsonObject() });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action != "add-block")
            return Task.FromResult(new ActionResult { Success = false });

        var toolName = data?["toolName"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(toolName))
            return Task.FromResult(new ActionResult { Success = false, Message = "工具名不能为空" });

        var mode = data?["mode"]?.ToString() ?? "block";
        var profile = _engine.ToolProfiles.GetProfile(_selectedProfile);
        if (profile == null)
            return Task.FromResult(new ActionResult { Success = false });

        if (mode == "unblock")
        {
            profile.UnblockedTools.Add(toolName);
            profile.BlockedTools.Remove(toolName);
        }
        else
        {
            profile.BlockedTools.Add(toolName);
            profile.UnblockedTools.Remove(toolName);
        }

        _engine.ToolProfiles.Save();
        return Task.FromResult(new ActionResult { Success = true, Message = $"已添加: {toolName} ({mode})" });
    }
}
