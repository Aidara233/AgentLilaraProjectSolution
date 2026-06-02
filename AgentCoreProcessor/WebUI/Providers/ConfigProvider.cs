using AgentLilara.PluginSDK.WebUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.WebUI.Providers;

internal class ConfigProvider : IWebUIProvider
{
    private readonly Dictionary<string, string> _coreFiles = new()
    {
        ["Base"] = "Core/Base.json",
        ["ExpressCore"] = "Core/ExpressCore.json",
        ["WorkingCore"] = "Core/WorkingCore.json",
        ["SystemCore"] = "Core/SystemCore.json",
        ["SubAgentCore"] = "Core/SubAgentCore.json",
        ["WeightCore"] = "Core/WeightCore.json",
        ["MemoryExtractionCore"] = "Core/MemoryExtractionCore.json",
        ["ReviewCore"] = "Core/ReviewCore.json",
        ["SleepTalkCore"] = "Core/SleepTalkCore.json",
        ["SummarizationCore"] = "Core/SummarizationCore.json",
    };

    private readonly Dictionary<string, string> _serviceFiles = new()
    {
        ["EmbeddingProvider"] = "Core/EmbeddingProvider.json",
        ["VisionProvider"] = "Core/VisionProvider.json",
        ["OcrProvider"] = "Core/OcrProvider.json",
    };

    private readonly Dictionary<string, string> _engineFiles = new()
    {
        ["EngineConfig"] = "Engine/EngineConfig.json",
        ["ImpulseConfig"] = "Engine/ImpulseConfig.json",
        ["TrustProgression"] = "Engine/TrustProgressionConfig.json",
        ["VisionEngine"] = "Engine/VisionEngineConfig.json",
        ["SignalFilter"] = "Engine/SignalFilter.json",
        ["DreamConfig"] = "Dream/DreamConfig.json",
        ["ComponentConfig"] = "Engine/ComponentConfig.json",
    };

    private readonly Dictionary<string, string> _otherFiles = new()
    {
        ["CommandConfig"] = "Command/CommandConfig.json",
        ["Adapter_qq-main"] = "Adapter/qq-main.json",
    };

    private readonly string _storageRoot;

    public string Id => "config";
    public string DisplayName => "配置管理";
    public IReadOnlyList<PageDefinition> Pages => _pages;
    private readonly List<PageDefinition> _pages;

    public ConfigProvider()
    {
        _storageRoot = PathConfig.StoragePath;
        _pages = new List<PageDefinition>
        {
            BuildOverview(),
            BuildGroupList("core", _coreFiles, "Core 模型调用配置"),
            BuildGroupEdit("core", _coreFiles),
            BuildGroupList("services", _serviceFiles, "服务接入配置"),
            BuildGroupEdit("services", _serviceFiles),
            BuildGroupList("engine", _engineFiles, "引擎行为配置"),
            BuildGroupEdit("engine", _engineFiles),
            BuildGroupList("other", _otherFiles, "其他配置"),
            BuildGroupEdit("other", _otherFiles),
        };
    }

    private PageDefinition BuildOverview()
    {
        return new PageDefinition
        {
            Route = "config",
            Meta = new PageMeta
            {
                Title = "配置管理",
                Icon = "bi-gear",
                ShowInNav = true,
                Group = "调试",
                Order = 113,
            },
            Cards = new List<CardDefinition>
            {
                new()
                {
                    Id = "cfg-overview-table",
                    Type = CardType.Table,
                    Title = "配置分类",
                    DataSourceId = "cfg-overview",
                    Schema = new TableSchema
                    {
                        Columns = new List<ColumnDef>
                        {
                            new() { Field = "name", Header = "分类" },
                            new() { Field = "count", Header = "文件数" },
                            new() { Field = "desc", Header = "说明" },
                        },
                        Searchable = false,
                        Paginated = false,
                    },
                    Layout = new CardLayout { MinWidth = "400px" },
                },
            },
            DataSources = new List<DataSourceDefinition>
            {
                new()
                {
                    Id = "cfg-overview",
                    Source = new ConfigOverviewSource(_coreFiles, _serviceFiles, _engineFiles, _otherFiles),
                },
            },
        };
    }

    private PageDefinition BuildGroupList(string group, Dictionary<string, string> files, string title)
    {
        return new PageDefinition
        {
            Route = $"config/{group}",
            LayoutType = PageLayoutType.Sidebar,
            Meta = new PageMeta
            {
                Title = title,
                Icon = "bi-folder",
                ShowInNav = false,
                Group = "配置",
            },
            Cards = new List<CardDefinition>
            {
                new()
                {
                    Id = $"{group}-list",
                    Type = CardType.Table,
                    Title = $"{title} ({files.Count} 个文件)",
                    DataSourceId = $"{group}-list",
                    Schema = new TableSchema
                    {
                        Columns = new List<ColumnDef>
                        {
                            new() { Field = "name", Header = "文件名" },
                            new() { Field = "path", Header = "路径" },
                        },
                        Searchable = true,
                        Paginated = false,
                    },
                    LinkEvent = "config-selected",
                    Layout = new CardLayout { PreferredCols = 3, GridColumnStart = 1 },
                },
                new()
                {
                    Id = $"{group}-detail",
                    Type = CardType.PropertyGrid,
                    Title = "属性编辑",
                    DataSourceId = $"{group}-detail",
                    ListenEvent = "config-selected",
                    Schema = new PropertyGridSchema
                    {
                        Description = "选择左侧配置文件进行编辑。修改后点击保存。",
                        SubmitLabel = "保存修改",
                    },
                    Layout = new CardLayout { PreferredCols = 9 },
                },
            },
            DataSources = new List<DataSourceDefinition>
            {
                new() { Id = $"{group}-list", Source = new ConfigListSource(files, group) },
                new() { Id = $"{group}-detail", Source = new ConfigEditSource(_storageRoot, files) },
            },
        };
    }

    private PageDefinition BuildGroupEdit(string group, Dictionary<string, string> files)
    {
        return new PageDefinition
        {
            Route = $"config/{group}/{{name}}",
            Meta = new PageMeta
            {
                Title = "编辑配置",
                Icon = "bi-pencil",
                ShowInNav = false,
            },
            Cards = new List<CardDefinition>
            {
                new()
                {
                    Id = $"{group}-editor",
                    Type = CardType.PropertyGrid,
                    Title = "配置编辑",
                    DataSourceId = $"{group}-editor",
                    Schema = new PropertyGridSchema { SubmitLabel = "保存修改" },
                    Layout = new CardLayout { MinWidth = "500px" },
                },
            },
            DataSources = new List<DataSourceDefinition>
            {
                new() { Id = $"{group}-editor", Source = new ConfigEditSource(_storageRoot, files) },
            },
        };
    }
}

// ---- sources ----

internal class ConfigOverviewSource : IDataSource
{
    private readonly Dictionary<string, string> _core, _services, _engine, _other;

    public ConfigOverviewSource(Dictionary<string, string> core, Dictionary<string, string> services,
        Dictionary<string, string> engine, Dictionary<string, string> other)
    {
        _core = core; _services = services; _engine = engine; _other = other;
    }

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var rows = new JsonArray
        {
            MakeRow("Core 模型调用", _core.Count, "模型/提示词/参数", "/p/config/core"),
            MakeRow("服务接入", _services.Count, "Embedding/Vision/OCR", "/p/config/services"),
            MakeRow("引擎行为", _engine.Count, "引擎/冲动/信任/梦境/视觉", "/p/config/engine"),
            MakeRow("其他", _other.Count, "命令/工具/适配器", "/p/config/other"),
        };

        return Task.FromResult(new DataResult { Data = rows, TotalCount = rows.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false, Message = "不支持" });

    private static JsonObject MakeRow(string name, int count, string desc, string link)
        => new()
        {
            ["name"] = name,
            ["count"] = count,
            ["desc"] = desc,
            ["_link"] = link,
        };
}

internal class ConfigListSource : IDataSource
{
    private readonly Dictionary<string, string> _files;
    private readonly string _group;

    public ConfigListSource(Dictionary<string, string> files, string group)
    {
        _files = files; _group = group;
    }

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var rows = new JsonArray();
        foreach (var (name, relPath) in _files)
        {
            var fullPath = System.IO.Path.Combine(PathConfig.StoragePath, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var exists = File.Exists(fullPath);
            var size = exists ? new FileInfo(fullPath).Length : 0;
            rows.Add(new JsonObject
            {
                ["name"] = name,
                ["path"] = relPath,
                ["size"] = FormatSize(size),
                ["exists"] = exists,
            });
        }

        if (!string.IsNullOrEmpty(query?.Search))
        {
            var search = query.Search.ToLowerInvariant();
            rows = new JsonArray(rows.Where(r =>
                r != null && (
                (r["name"]?.ToString() ?? "").ToLowerInvariant().Contains(search) ||
                (r["path"]?.ToString() ?? "").ToLowerInvariant().Contains(search))
            ).ToArray());
        }

        return Task.FromResult(new DataResult { Data = rows, TotalCount = rows.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false, Message = "不支持" });

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}

internal class ConfigEditSource : IDataSource
{
    private readonly string _storageRoot;
    private readonly Dictionary<string, string>? _files;
    private string? _currentFile;

    public ConfigEditSource(string storageRoot, Dictionary<string, string>? files = null)
    {
        _storageRoot = storageRoot;
        _files = files;
    }

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        string? fileKey = null;

        if (query?.RouteParams?.TryGetValue("name", out var rn) == true)
            fileKey = rn;

        if (fileKey == null && query?.Extra is JsonObject extra)
            fileKey = extra["name"]?.ToString();

        string? relPath = null;
        if (_files != null && fileKey != null)
            _files.TryGetValue(fileKey, out relPath);

        if (relPath == null)
        {
            return Task.FromResult(new DataResult
            {
                Data = new JsonObject { ["properties"] = new JsonArray() },
            });
        }

        var fullPath = System.IO.Path.Combine(_storageRoot, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return Task.FromResult(new DataResult
            {
                Data = new JsonObject { ["properties"] = new JsonArray() },
            });
        }

        try
        {
            _currentFile = fullPath;
            var json = JsonNode.Parse(File.ReadAllText(fullPath));
            if (json is not JsonObject root)
            {
                return Task.FromResult(new DataResult
                {
                    Data = new JsonObject { ["properties"] = new JsonArray() },
                });
            }

            var props = FlattenProperties(root);
            return Task.FromResult(new DataResult
            {
                Data = new JsonObject
                {
                    ["properties"] = props,
                    ["_filePath"] = fullPath,
                },
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DataResult
            {
                Data = new JsonObject
                {
                    ["properties"] = new JsonArray(),
                    ["_error"] = ex.Message,
                },
            });
        }
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action != "save" || _currentFile == null || data is not JsonObject payload)
            return new ActionResult { Success = false, Message = "无效请求" };

        var changes = payload["changes"]?.AsObject();
        if (changes == null || changes.Count == 0)
            return new ActionResult { Success = false, Message = "无变更" };

        try
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(_currentFile, ct));
            if (json is not JsonObject root)
                return new ActionResult { Success = false, Message = "配置文件解析失败" };

            foreach (var (key, value) in changes)
            {
                var dotIdx = key.IndexOf('.');
                if (dotIdx > 0 && root.ContainsKey(key[..dotIdx]) && root[key[..dotIdx]] is JsonObject)
                {
                    var parentKey = key[..dotIdx];
                    var childKey = key[(dotIdx + 1)..];
                    var parent = root[parentKey]!.AsObject();
                    ReplaceValue(parent, childKey, value!);
                }
                else if (root.ContainsKey(key))
                {
                    ReplaceValue(root, key, value!);
                }
            }

            await File.WriteAllTextAsync(_currentFile, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct);
            return new ActionResult { Success = true, Message = "配置已保存" };
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Message = $"保存失败: {ex.Message}" };
        }
    }

    private static void ReplaceValue(JsonObject obj, string key, JsonNode newValue)
    {
        if (!obj.ContainsKey(key)) return;
        var existing = obj[key];
        if (existing == null) { obj[key] = newValue; return; }

        obj[key] = existing.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number =>
                JsonValue.Create(double.TryParse(newValue.ToString(), out var d) ? d : (object)newValue.ToString()),
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False =>
                JsonValue.Create(bool.TryParse(newValue.ToString(), out var b) ? b : false),
            System.Text.Json.JsonValueKind.Null =>
                newValue.DeepClone(),
            _ => JsonValue.Create(newValue.ToString()),
        };
    }

    internal static JsonArray FlattenProperties(JsonObject root)
    {
        var arr = new JsonArray();

        foreach (var (key, value) in root)
        {
            if (value is JsonObject nested && IsSimpleNested(nested))
            {
                foreach (var (nk, nv) in nested)
                    arr.Add(MakeProp($"{key}.{nk}", nv, key, nk));
            }
            else
            {
                arr.Add(MakeProp(key, value));
            }
        }

        return arr;
    }

    private static bool IsSimpleNested(JsonObject obj)
        => obj.All(kv => kv.Value is not JsonObject && kv.Value is not JsonArray);

    private static JsonObject MakeProp(string name, JsonNode? value, string? parentLabel = null, string? childName = null)
    {
        string label;
        string type;
        var sensitive = IsSensitiveKey(childName ?? name);

        if (value == null)
        {
            type = "text";
            label = parentLabel != null ? $"{parentLabel} › {childName}" : name;
        }
        else if (value is JsonValue jv)
        {
            type = jv.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "toggle",
                System.Text.Json.JsonValueKind.Number => "number",
                _ => sensitive ? "password" : "text",
            };
            label = parentLabel != null ? $"{parentLabel} › {childName}" : name;
        }
        else if (value is JsonArray or JsonObject)
        {
            type = "json";
            label = parentLabel != null ? $"{parentLabel} › {childName}" : name;
        }
        else
        {
            type = "text";
            label = parentLabel != null ? $"{parentLabel} › {childName}" : name;
        }

        return new JsonObject
        {
            ["name"] = name,
            ["label"] = label,
            ["value"] = value?.DeepClone(),
            ["type"] = type,
            ["sensitive"] = sensitive,
        };
    }

    private static bool IsSensitiveKey(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("apikey") || lower.Contains("api_key") || lower.Contains("secret")
            || lower.Contains("password") || lower.Contains("token");
    }
}
