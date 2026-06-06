using AgentLilara.PluginSDK.WebUI;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Tool;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.WebUI.Providers;

internal class ModesProvider : IWebUIProvider
{
    public string Id => "modes";
    public string DisplayName => "模式管理";
    public IReadOnlyList<PageDefinition> Pages => _pages;
    private readonly List<PageDefinition> _pages;

    public ModesProvider()
    {
        _pages = new List<PageDefinition> { BuildModesPage() };
    }

    private static PageDefinition BuildModesPage()
    {
        return new PageDefinition
        {
            Route = "modes",
            LayoutType = PageLayoutType.Sidebar,
            Meta = new PageMeta
            {
                Title = "模式管理",
                Icon = "bi-sliders",
                ShowInNav = true,
                Group = "调试",
                Order = 114,
            },
            Cards = new List<CardDefinition>
            {
                new()
                {
                    Id = "modes-list-card",
                    Type = CardType.Table,
                    Title = "模式列表",
                    DataSourceId = "modes-list",
                    Schema = new TableSchema
                    {
                        Columns = new List<ColumnDef>
                        {
                            new() { Field = "displayName", Header = "名称" },
                            new() { Field = "id", Header = "ID" },
                            new() { Field = "metaType", Header = "类型" },
                            new() { Field = "maxRounds", Header = "轮次" },
                        },
                        Searchable = false,
                        Paginated = false,
                        RowActions = new List<RowAction>
                        {
                            new() { Id = "delete-mode", Label = "删除", Icon = "bi-trash", Danger = true, Confirm = "确定删除此模式？" },
                            new() { Id = "reset-mode", Label = "重置为默认", Icon = "bi-arrow-counterclockwise" },
                        },
                    },
                    LinkEvent = "mode-selected",
                    Layout = new CardLayout { PreferredCols = 3, GridColumnStart = 1 },
                },
                new()
                {
                    Id = "modes-edit-card",
                    Type = CardType.Form,
                    Title = "模式属性",
                    DataSourceId = "modes-edit",
                    ListenEvent = "mode-selected",
                    Schema = new FormSchema
                    {
                        Fields = new List<FormField>
                        {
                            new() { Field = "id", Label = "模式 ID", Type = FormFieldType.Text, Required = true, Description = "英文标识符，创建后不可修改" },
                            new() { Field = "displayName", Label = "显示名称", Type = FormFieldType.Text, Required = true },
                            new() { Field = "description", Label = "描述", Type = FormFieldType.TextArea },
                            new() { Field = "metaType", Label = "元类型", Type = FormFieldType.Select, Required = true, Options = new List<SelectOption>
                            {
                                new() { Value = "Express", Label = "Express（轻量对话）" },
                                new() { Value = "Working", Label = "Working（工作模式）" },
                            }},
                            new() { Field = "maxRounds", Label = "最大轮次", Type = FormFieldType.Number, Required = true },
                            new() { Field = "toolDefaults", Label = "默认工具行为", Type = FormFieldType.Select, Required = true, Options = new List<SelectOption>
                            {
                                new() { Value = "enabled", Label = "默认启用" },
                                new() { Value = "disabled", Label = "默认禁用" },
                            }},
                        },
                        ShowSubmit = true,
                        ShowReset = true,
                    },
                    Layout = new CardLayout { PreferredCols = 9 },
                },
                new()
                {
                    Id = "modes-tools-card",
                    Type = CardType.Table,
                    Title = "工具配置",
                    DataSourceId = "modes-tools",
                    ListenEvent = "mode-selected",
                    Schema = new TableSchema
                    {
                        Columns = new List<ColumnDef>
                        {
                            new() { Field = "name", Header = "工具名" },
                            new() { Field = "description", Header = "描述" },
                            new() { Field = "state", Header = "状态", Format = ColumnFormat.Badge },
                            new() { Field = "group", Header = "分组" },
                        },
                        Searchable = true,
                        Paginated = false,
                        RowActions = new List<RowAction>
                        {
                            new() { Id = "tool-enable", Label = "启用" },
                            new() { Id = "tool-disable", Label = "禁用" },
                            new() { Id = "tool-default", Label = "使用默认" },
                        },
                    },
                    Layout = new CardLayout { PreferredCols = 9 },
                },
            },
            DataSources = new List<DataSourceDefinition>
            {
                new() { Id = "modes-list", Source = new ModesListSource() },
                new() { Id = "modes-edit", Source = new ModesEditSource() },
                new() { Id = "modes-tools", Source = new ModesToolsSource() },
            },
        };
    }
}

// ---- ModesListSource ----

internal class ModesListSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var modes = ModeConfigLoader.GetAllModes();
        var rows = new JsonArray();
        foreach (var m in modes)
        {
            var toolCount = m.Tools.Count;
            var defaultCount = m.ToolDefaults.Equals("enabled", StringComparison.OrdinalIgnoreCase)
                ? ToolRegistry.All.Count - toolCount
                : 0;
            rows.Add(new JsonObject
            {
                ["id"] = m.Id,
                ["displayName"] = m.DisplayName,
                ["metaType"] = m.MetaType,
                ["description"] = m.Description,
                ["maxRounds"] = m.MaxRounds,
                ["toolDefaults"] = m.ToolDefaults,
                ["toolCount"] = toolCount + defaultCount,
            });
        }
        return Task.FromResult(new DataResult { Data = rows, TotalCount = rows.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action == "delete-mode" && data is JsonObject obj && obj["id"]?.ToString() is string modeId)
        {
            if (IsBuiltinMode(modeId))
                return Task.FromResult(new ActionResult { Success = false, Message = "内置模式不可删除，只能重置为默认" });

            var config = ModeConfigLoader.Load();
            config.Modes.RemoveAll(m => m.Id == modeId);
            ModeConfigLoader.Save(config);
            return Task.FromResult(new ActionResult { Success = true, Message = $"模式 {modeId} 已删除" });
        }

        if (action == "reset-mode" && data is JsonObject obj2 && obj2["id"]?.ToString() is string resetId)
        {
            var templateMode = GetTemplateMode(resetId);
            if (templateMode == null)
                return Task.FromResult(new ActionResult { Success = false, Message = "模板中不存在此模式，无法重置" });

            var config = ModeConfigLoader.Load();
            var idx = config.Modes.FindIndex(m => m.Id == resetId);
            if (idx >= 0)
                config.Modes[idx] = templateMode;
            else
                config.Modes.Add(templateMode);
            ModeConfigLoader.Save(config);
            return Task.FromResult(new ActionResult { Success = true, Message = $"模式 {resetId} 已重置为默认" });
        }

        return Task.FromResult(new ActionResult { Success = false, Message = "不支持的操作" });
    }

    internal static bool IsBuiltinMode(string modeId)
        => GetTemplateMode(modeId) != null;

    internal static ModeDefinition? GetTemplateMode(string modeId)
    {
        var templatePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "templates", "Engine", "ModeConfig.json");
        if (!File.Exists(templatePath)) return null;
        try
        {
            var json = File.ReadAllText(templatePath);
            var config = JsonConvert.DeserializeObject<ModeConfig>(json);
            return config?.Modes.FirstOrDefault(m =>
                string.Equals(m.Id, modeId, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }
}

// ---- ModesEditSource ----

internal class ModesEditSource : IDataSource
{
    private string? _lastModeId;

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        string? modeId = null;
        if (query?.Extra is JsonObject extra)
            modeId = extra["id"]?.ToString();

        // 缓存最后选中的模式，防止操作后丢失选中状态
        modeId ??= _lastModeId;
        if (!string.IsNullOrEmpty(modeId))
            _lastModeId = modeId;

        if (string.IsNullOrEmpty(modeId))
            return Task.FromResult(new DataResult { Data = new JsonObject { ["_empty"] = true } });

        var mode = ModeConfigLoader.GetMode(modeId);
        if (mode == null)
            return Task.FromResult(new DataResult { Data = new JsonObject { ["_error"] = "模式不存在" } });

        return Task.FromResult(new DataResult
        {
            Data = new JsonObject
            {
                ["id"] = mode.Id,
                ["displayName"] = mode.DisplayName,
                ["description"] = mode.Description,
                ["metaType"] = mode.MetaType,
                ["maxRounds"] = mode.MaxRounds,
                ["toolDefaults"] = mode.ToolDefaults,
            },
        });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action != "save" || data is not JsonObject payload)
            return Task.FromResult(new ActionResult { Success = false, Message = "无效请求" });

        var modeId = payload["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(modeId))
            return Task.FromResult(new ActionResult { Success = false, Message = "模式 ID 不能为空" });

        var config = ModeConfigLoader.Load();
        var existing = config.Modes.FirstOrDefault(m =>
            string.Equals(m.Id, modeId, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.DisplayName = payload["displayName"]?.ToString() ?? existing.DisplayName;
            existing.Description = payload["description"]?.ToString() ?? existing.Description;
            existing.MetaType = payload["metaType"]?.ToString() ?? existing.MetaType;
            if (int.TryParse(payload["maxRounds"]?.ToString(), out var mr) && mr > 0)
                existing.MaxRounds = mr;
            existing.ToolDefaults = payload["toolDefaults"]?.ToString() ?? existing.ToolDefaults;
            ModeConfigLoader.Save(config);
            return Task.FromResult(new ActionResult { Success = true, Message = $"模式 {modeId} 已更新" });
        }
        else
        {
            var newMode = new ModeDefinition
            {
                Id = modeId,
                DisplayName = payload["displayName"]?.ToString() ?? modeId,
                Description = payload["description"]?.ToString() ?? "",
                MetaType = payload["metaType"]?.ToString() ?? "Working",
                MaxRounds = int.TryParse(payload["maxRounds"]?.ToString(), out var mr2) && mr2 > 0 ? mr2 : 10,
                ToolDefaults = payload["toolDefaults"]?.ToString() ?? "disabled",
            };
            config.Modes.Add(newMode);
            ModeConfigLoader.Save(config);
            return Task.FromResult(new ActionResult { Success = true, Message = $"模式 {modeId} 已创建" });
        }
    }
}

// ---- ModesToolsSource ----

internal class ModesToolsSource : IDataSource
{
    private string? _lastModeId;

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        string? modeId = null;
        if (query?.Extra is JsonObject extra)
            modeId = extra["id"]?.ToString();

        // 缓存最后选中的模式，防止操作后丢失选中状态
        modeId ??= _lastModeId;
        if (!string.IsNullOrEmpty(modeId))
            _lastModeId = modeId;

        if (string.IsNullOrEmpty(modeId))
            return Task.FromResult(new DataResult { Data = new JsonArray() });

        var mode = ModeConfigLoader.GetMode(modeId);
        var defaultLabel = mode?.ToolDefaults == "enabled" ? "默认(启用)" : "默认(禁用)";
        var allTools = ToolRegistry.All;
        var rows = new JsonArray();
        foreach (var (name, tool) in allTools)
        {
            var meta = ToolRegistry.GetMeta(name);
            var state = ModeConfigLoader.GetToolState(modeId, name);
            rows.Add(new JsonObject
            {
                ["name"] = name,
                ["description"] = tool.Description ?? "",
                ["state"] = state,
                ["stateLabel"] = state switch
                {
                    "enabled" => "[启用]",
                    "disabled" => "[禁用]",
                    _ => defaultLabel,
                },
                ["group"] = meta?.Group ?? "-",
            });
        }

        if (!string.IsNullOrEmpty(query?.Search))
        {
            var search = query.Search.ToLowerInvariant();
            rows = new JsonArray(rows.Where(r => r != null && (
                (r["name"]?.ToString() ?? "").ToLowerInvariant().Contains(search) ||
                (r["description"]?.ToString() ?? "").ToLowerInvariant().Contains(search) ||
                (r["group"]?.ToString() ?? "").ToLowerInvariant().Contains(search))
            ).ToArray());
        }

        return Task.FromResult(new DataResult { Data = rows, TotalCount = rows.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (data is not JsonObject obj)
            return Task.FromResult(new ActionResult { Success = false, Message = "无效请求" });

        // modeId comes from payload or cached last selection
        var modeId = obj["id"]?.ToString() ?? _lastModeId;
        var toolName = obj["name"]?.ToString();

        if (string.IsNullOrEmpty(modeId) || string.IsNullOrEmpty(toolName))
            return Task.FromResult(new ActionResult { Success = false, Message = "缺少模式或工具标识" });

        var config = ModeConfigLoader.Load();
        var mode = config.Modes.FirstOrDefault(m =>
            string.Equals(m.Id, modeId, StringComparison.OrdinalIgnoreCase));
        if (mode == null)
            return Task.FromResult(new ActionResult { Success = false, Message = "模式不存在" });

        switch (action)
        {
            case "tool-enable":
                mode.Tools[toolName] = "enabled";
                ModeConfigLoader.Save(config);
                return Task.FromResult(new ActionResult { Success = true, Message = $"{toolName} → 已启用" });
            case "tool-disable":
                mode.Tools[toolName] = "disabled";
                ModeConfigLoader.Save(config);
                return Task.FromResult(new ActionResult { Success = true, Message = $"{toolName} → 已禁用" });
            case "tool-default":
                mode.Tools.Remove(toolName);
                ModeConfigLoader.Save(config);
                return Task.FromResult(new ActionResult { Success = true, Message = $"{toolName} → 恢复默认" });
            default:
                return Task.FromResult(new ActionResult { Success = false, Message = "不支持的操作" });
        }
    }
}
