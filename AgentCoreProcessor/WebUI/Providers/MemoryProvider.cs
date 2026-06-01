using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class MemoryProvider : IWebUIProvider
{
    public string Id => "core-memory";
    public string DisplayName => "记忆";

    private readonly MasterEngine _engine;

    public MemoryProvider(MasterEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<PageDefinition> Pages => new List<PageDefinition>
    {
        BuildMainPage(),
        BuildDetailPage(),
        BuildTempPage(),
        BuildPersonaPage(),
        BuildPeoplePage(),
        BuildGraphPage(),
    };

    // ================ 主库浏览 ================

    private PageDefinition BuildMainPage() => new()
    {
        Route = "memory",
        Meta = new PageMeta { Title = "主库浏览", Icon = "bi-database", Group = "记忆", Order = 80 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "main-stats", Type = CardType.Status, DataSourceId = "main-stats", Title = "统计",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "total", Label = "总数" },
                        new() { Field = "type_dist", Label = "类型分布", IsMultiline = true }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            },
            new()
            {
                Id = "main-table", Type = CardType.Table, DataSourceId = "main-table", Title = "主记忆库",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "60px" },
                        new() { Field = "type", Header = "类型", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "subject", Header = "主题", Width = "120px" },
                        new() { Field = "content", Header = "内容" },
                        new() { Field = "importance", Header = "重要度", Width = "80px" },
                        new() { Field = "certainty", Header = "确定性", Width = "60px", Format = ColumnFormat.Badge },
                        new() { Field = "created", Header = "创建", Width = "100px" }
                    },
                    DefaultPageSize = 25,
                    Filters = new()
                    {
                        new() { Field = "type", Label = "全部类型", Options = new()
                        {
                            new() { Value = "knowledge", Label = "knowledge" },
                            new() { Value = "fact", Label = "fact" },
                            new() { Value = "feedback", Label = "feedback" },
                            new() { Value = "inference", Label = "inference" },
                            new() { Value = "event", Label = "event" },
                            new() { Value = "state", Label = "state" },
                            new() { Value = "preference", Label = "preference" }
                        }},
                    },
                    RowActions = new()
                    {
                        new() { Id = "delete", Label = "删除", Danger = true, Confirm = "确认删除这条记忆？" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "main-stats", Source = new MainMemoryStatsSource(_engine) },
            new() { Id = "main-table", Source = new MainMemorySource(_engine) }
        }
    };

    // ================ 记忆详情 ================

    private PageDefinition BuildDetailPage() => new()
    {
        Route = "memory/{id}",
        Meta = new PageMeta { Title = "记忆详情", ShowInNav = false },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "detail-info", Type = CardType.Status, DataSourceId = "detail-info", Title = "基本信息",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "content", Label = "内容", IsMultiline = true },
                        new() { Field = "type", Label = "类型", Type = StatusFieldType.Badge },
                        new() { Field = "subject", Label = "主题" },
                        new() { Field = "importance", Label = "重要度" },
                        new() { Field = "certainty", Label = "确定性", Type = StatusFieldType.Badge },
                        new() { Field = "person_id", Label = "关联人物" },
                        new() { Field = "channel_id", Label = "关联频道" },
                        new() { Field = "is_derived", Label = "衍生记忆", Type = StatusFieldType.Badge },
                        new() { Field = "is_persistent", Label = "持久", Type = StatusFieldType.Badge },
                        new() { Field = "expires", Label = "过期时间" },
                        new() { Field = "last_dream", Label = "最后做梦" },
                        new() { Field = "last_access", Label = "最后访问" },
                        new() { Field = "source_msg", Label = "来源消息" },
                        new() { Field = "created", Label = "创建时间" }
                    },
                    Actions = new()
                    {
                        new() { Id = "delete", Label = "删除记忆", Danger = true, Confirm = "确认删除这条记忆？关联也会被清理。" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "detail-source", Type = CardType.Table, DataSourceId = "detail-source", Title = "源记忆",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "60px" },
                        new() { Field = "type", Header = "类型", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "content", Header = "内容" }
                    },
                    Paginated = false, Searchable = false
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "detail-derived", Type = CardType.Table, DataSourceId = "detail-derived", Title = "衍生记忆",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "60px" },
                        new() { Field = "type", Header = "类型", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "subject", Header = "主题", Width = "100px" },
                        new() { Field = "content", Header = "内容" }
                    },
                    Paginated = false, Searchable = false
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "detail-links", Type = CardType.Table, DataSourceId = "detail-links", Title = "关联记忆",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "60px" },
                        new() { Field = "type", Header = "类型", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "link_type", Header = "关联类型", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "relevance", Header = "强度", Width = "70px" },
                        new() { Field = "content", Header = "内容" }
                    },
                    Paginated = false, Searchable = false
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "detail-info", Source = new MemoryDetailInfoSource(_engine) },
            new() { Id = "detail-source", Source = new MemoryDetailSourceSource(_engine) },
            new() { Id = "detail-derived", Source = new MemoryDetailDerivedSource(_engine) },
            new() { Id = "detail-links", Source = new MemoryDetailLinksSource(_engine) }
        }
    };

    // ================ 临时库 ================

    private PageDefinition BuildTempPage() => new()
    {
        Route = "memory/temp",
        Meta = new PageMeta { Title = "临时库", Icon = "bi-clock-history", Group = "记忆", Order = 81 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "temp-stats", Type = CardType.Status, DataSourceId = "temp-stats", Title = "统计",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "total", Label = "总数" },
                        new() { Field = "high_conf", Label = "高置信" },
                        new() { Field = "low_conf", Label = "低置信" }
                    },
                    Actions = new()
                    {
                        new() { Id = "clear", Label = "清空全部", Danger = true, Confirm = "确认清空所有临时记忆？此操作不可撤销。" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            },
            new()
            {
                Id = "temp-table", Type = CardType.Table, DataSourceId = "temp-table", Title = "临时记忆库",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "60px" },
                        new() { Field = "type", Header = "类型", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "subject", Header = "主题", Width = "120px" },
                        new() { Field = "content", Header = "内容" },
                        new() { Field = "certainty", Header = "确定性", Width = "60px", Format = ColumnFormat.Badge },
                        new() { Field = "person_id", Header = "人物", Width = "60px" },
                        new() { Field = "channel_id", Header = "频道", Width = "60px" },
                        new() { Field = "created", Header = "创建", Width = "100px" }
                    },
                    DefaultPageSize = 25,
                    Filters = new()
                    {
                        new() { Field = "type", Label = "全部类型", Options = new()
                        {
                            new() { Value = "knowledge", Label = "knowledge" },
                            new() { Value = "fact", Label = "fact" },
                            new() { Value = "feedback", Label = "feedback" },
                            new() { Value = "inference", Label = "inference" },
                            new() { Value = "event", Label = "event" },
                            new() { Value = "state", Label = "state" },
                            new() { Value = "preference", Label = "preference" }
                        }},
                    },
                    RowActions = new()
                    {
                        new() { Id = "delete", Label = "删除", Danger = true, Confirm = "确认删除？" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "temp-stats", Source = new TempMemoryStatsSource(_engine) },
            new() { Id = "temp-table", Source = new TempMemorySource(_engine) }
        }
    };

    // ================ 人设记忆 ================

    private PageDefinition BuildPersonaPage() => new()
    {
        Route = "memory/persona",
        Meta = new PageMeta { Title = "人设记忆", Icon = "bi-person-badge", Group = "记忆", Order = 82 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "persona-add", Type = CardType.Form, DataSourceId = "persona-form", Title = "新增人设记忆",
                Schema = new FormSchema
                {
                    Fields = new()
                    {
                        new() { Field = "category", Label = "分类", Type = FormFieldType.Text, Placeholder = "如：经历、偏好、背景" },
                        new() { Field = "content", Label = "内容", Type = FormFieldType.TextArea, Placeholder = "人设记忆内容", Required = true }
                    },
                    ShowReset = true
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "persona-stats", Type = CardType.Status, DataSourceId = "persona-stats", Title = "统计",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "total", Label = "总数" },
                        new() { Field = "categories", Label = "分类", IsMultiline = true }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "persona-table", Type = CardType.Table, DataSourceId = "persona-table", Title = "人设记忆库",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "60px" },
                        new() { Field = "category", Header = "分类", Width = "100px", Format = ColumnFormat.Badge },
                        new() { Field = "content", Header = "内容" },
                        new() { Field = "created", Header = "创建", Width = "100px" }
                    },
                    DefaultPageSize = 25,
                    Searchable = false,
                    RowActions = new()
                    {
                        new() { Id = "edit", Label = "编辑" },
                        new() { Id = "delete", Label = "删除", Danger = true, Confirm = "确认删除？" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "persona-form", Source = new PersonaFormSource(_engine) },
            new() { Id = "persona-stats", Source = new PersonaStatsSource(_engine) },
            new() { Id = "persona-table", Source = new PersonaTableSource(_engine) }
        }
    };

    // ================ 自然人管理 ================

    private PageDefinition BuildPeoplePage() => new()
    {
        Route = "memory/people",
        Meta = new PageMeta { Title = "自然人管理", Icon = "bi-people", Group = "记忆", Order = 83 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "people-table", Type = CardType.Table, DataSourceId = "people-list", Title = "自然人列表",
                LinkEvent = "people-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "60px" },
                        new() { Field = "name", Header = "名称" },
                        new() { Field = "trustLevel", Header = "信任", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "alertLevel", Header = "警报", Width = "60px", Format = ColumnFormat.Badge },
                        new() { Field = "fastMemory", Header = "速记" },
                    },
                    Searchable = true, Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 7 }
            },
            new()
            {
                Id = "people-edit", Type = CardType.Form, DataSourceId = "people-edit", Title = "编辑自然信息",
                ListenEvent = "people-selected",
                Schema = new FormSchema
                {
                    Fields = new()
                    {
                        new() { Field = "name", Label = "名称", Type = FormFieldType.Text },
                        new() { Field = "aliases", Label = "别称", Type = FormFieldType.Text, Placeholder = "逗号分隔" },
                        new() { Field = "trustLevel", Label = "信任等级", Type = FormFieldType.Select, Options = new()
                        {
                            new() { Value = "-2", Label = "敌对 (-2)" },
                            new() { Value = "-1", Label = "警惕 (-1)" },
                            new() { Value = "0", Label = "未知 (0)" },
                            new() { Value = "1", Label = "陌生人 (1)" },
                            new() { Value = "2", Label = "理解 (2)" },
                            new() { Value = "3", Label = "熟悉 (3)" },
                            new() { Value = "4", Label = "信任 (4)" },
                            new() { Value = "5", Label = "绝对信任 (5)" },
                        }},
                        new() { Field = "trustProgress", Label = "好感度", Type = FormFieldType.Number, Placeholder = "可负" },
                        new() { Field = "alertLevel", Label = "警报等级", Type = FormFieldType.Select, Options = new()
                        {
                            new() { Value = "0", Label = "0 - 无" },
                            new() { Value = "1", Label = "1 - 低" },
                            new() { Value = "2", Label = "2 - 中" },
                            new() { Value = "3", Label = "3 - 高" },
                            new() { Value = "4", Label = "4 - 紧急" },
                        }},
                        new() { Field = "fastMemory", Label = "速记", Type = FormFieldType.TextArea, Placeholder = "一句话概括" },
                        new() { Field = "accounts", Label = "绑定账号", Type = FormFieldType.TextArea, Readonly = true, Placeholder = "无绑定账号" },
                    },
                    ShowReset = false
                },
                Layout = new CardLayout { PreferredCols = 5 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "people-list", Source = new PeopleListSource(_engine) },
            new() { Id = "people-edit", Source = new PeopleEditSource(_engine) }
        }
    };

    // ================ 关联图谱（重定向） ================

    private PageDefinition BuildGraphPage() => new()
    {
        Route = "memory/graph",
        Meta = new PageMeta { Title = "关联图谱", Icon = "bi-diagram-3", Group = "记忆", Order = 86 },
        Cards = new List<CardDefinition>(),
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "graph-redirect", Source = new MemoryGraphRedirectSource() }
        }
    };
}


// ================ 主库数据源 ================

internal class MainMemorySource : IDataSource
{
    private readonly MasterEngine _engine;
    private List<MemoryEntry> _allCache = new();
    private DateTime _cacheTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);

    public MainMemorySource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        // 缓存全量数据减少DB查询
        if ((DateTime.Now - _cacheTime) > CacheTtl)
        {
            _allCache = await _engine.Memories.GetRecentAsync(10000);
            _cacheTime = DateTime.Now;
        }

        var filtered = Filter(_allCache, query);
        var total = filtered.Count;

        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 25;
        var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var arr = new JsonArray();
        foreach (var m in paged)
        {
            arr.Add(new JsonObject
            {
                ["id"] = m.Id,
                ["type"] = m.Type,
                ["subject"] = m.Subject ?? "",
                ["content"] = m.Content,
                ["importance"] = m.Importance.ToString("F2"),
                ["certainty"] = m.Certainty.ToString("F2"),
                ["person"] = m.PersonId?.ToString() ?? "—",
                ["created"] = m.CreatedAt.ToString("MM-dd HH:mm"),
                ["_link"] = $"/p/memory/{m.Id}"
            });
        }
        return new DataResult { Data = arr, TotalCount = total };
    }

    private static List<MemoryEntry> Filter(List<MemoryEntry> all, DataQuery? query)
    {
        var result = all.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var kw = query.Search.Trim();
            result = result.Where(m => m.Content.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        if (query?.Filters != null)
        {
            foreach (var f in query.Filters)
            {
                if (string.IsNullOrEmpty(f.Value)) continue;
                switch (f.Field)
                {
                    case "type":
                        result = result.Where(m => m.Type == f.Value);
                        break;
                    case "certainty":
                        if (float.TryParse(f.Value, out var certVal))
                            result = result.Where(m => m.Certainty >= certVal);
                        break;
                    case "person":
                        if (int.TryParse(f.Value, out var personId) && personId > 0)
                            result = result.Where(m => m.PersonId == personId);
                        break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(query?.SortBy) && query.SortBy == "importance")
            return query.SortDesc ? result.OrderByDescending(m => m.Importance).ToList() : result.OrderBy(m => m.Importance).ToList();

        return result.OrderByDescending(m => m.CreatedAt).ToList();
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action == "delete" && data != null)
        {
            var id = data["id"]?.GetValue<int>() ?? 0;
            if (id > 0)
            {
                var memory = await _engine.Memories.GetByIdAsync(id);
                if (memory != null)
                {
                    await _engine.Memories.DeleteAsync(memory);
                    await _engine.MemoryLinks.DeleteOrphanedForMemoryAsync(id);
                    _cacheTime = DateTime.MinValue; // 失效缓存
                    return new ActionResult { Success = true, Message = "已删除" };
                }
            }
        }
        return new ActionResult { Success = false, Message = "未知操作" };
    }
}

internal class MainMemoryStatsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public MainMemoryStatsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = await _engine.Memories.GetRecentAsync(10000);
        var typeDist = all.GroupBy(m => m.Type)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();

        var data = new JsonObject
        {
            ["total"] = all.Count.ToString(),
            ["type_dist"] = string.Join("\n", typeDist)
        };
        return new DataResult { Data = data };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}


// ================ 记忆详情数据源 ================

/// <summary>从 RouteParams 提取记忆 ID 并加载 MemoryEntry，供所有详情子数据源复用。</summary>
internal static class MemoryDetailHelper
{
    public static async Task<MemoryEntry?> GetMemoryAsync(MasterEngine engine, DataQuery? query)
    {
        var idStr = query?.RouteParams?.GetValueOrDefault("id");
        if (!int.TryParse(idStr, out var id) || id <= 0) return null;
        return await engine.Memories.GetByIdAsync(id);
    }
}

internal class MemoryDetailInfoSource : IDataSource
{
    private readonly MasterEngine _engine;
    public MemoryDetailInfoSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var m = await MemoryDetailHelper.GetMemoryAsync(_engine, query);
        if (m == null) return new DataResult { Data = new JsonObject { ["error"] = "记忆不存在" } };

        var data = new JsonObject
        {
            ["content"] = m.Content,
            ["type"] = m.Type,
            ["subject"] = m.Subject ?? "—",
            ["importance"] = m.Importance.ToString("F2"),
            ["certainty"] = m.Certainty,
            ["person_id"] = m.PersonId?.ToString() ?? "—",
            ["channel_id"] = m.ChannelId?.ToString() ?? "—",
            ["is_derived"] = m.IsDerived ? "是" : "否",
            ["is_persistent"] = m.IsPersistent ? "是" : "否",
            ["expires"] = m.ExpiresAt?.ToString("yyyy-MM-dd HH:mm") ?? "永不",
            ["last_dream"] = m.LastDreamTime?.ToString("MM-dd HH:mm") ?? "未处理",
            ["last_access"] = m.LastAccessedAt.ToString("MM-dd HH:mm"),
            ["source_msg"] = m.SourceMessageId?.ToString() ?? "—",
            ["created"] = m.CreatedAt.ToString("yyyy-MM-dd HH:mm")
        };
        return new DataResult { Data = data };
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var m = await MemoryDetailHelper.GetMemoryAsync(_engine, null);
        if (m == null) return new ActionResult { Success = false, Message = "记忆不存在" };

        if (action == "delete")
        {
            await _engine.Memories.DeleteAsync(m);
            await _engine.MemoryLinks.DeleteOrphanedForMemoryAsync(m.Id);
            return new ActionResult { Success = true, Message = "/p/memory" };
        }
        return new ActionResult { Success = false, Message = $"未知操作: {action}" };
    }
}

internal class MemoryDetailSourceSource : IDataSource
{
    private readonly MasterEngine _engine;
    public MemoryDetailSourceSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var m = await MemoryDetailHelper.GetMemoryAsync(_engine, query);
        var arr = new JsonArray();
        if (m != null && m.IsDerived && !string.IsNullOrEmpty(m.SourceMemoryIds))
        {
            try
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<List<int>>(m.SourceMemoryIds);
                if (ids != null)
                {
                    var sources = await _engine.Memories.GetByIdsAsync(ids);
                    foreach (var s in sources)
                    {
                        arr.Add(new JsonObject
                        {
                            ["id"] = s.Id,
                            ["type"] = s.Type,
                            ["content"] = s.Content.Length <= 120 ? s.Content : s.Content[..120] + "...",
                            ["_link"] = $"/p/memory/{s.Id}"
                        });
                    }
                }
            }
            catch { }
        }
        return new DataResult { Data = arr };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class MemoryDetailDerivedSource : IDataSource
{
    private readonly MasterEngine _engine;
    public MemoryDetailDerivedSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var m = await MemoryDetailHelper.GetMemoryAsync(_engine, query);
        var arr = new JsonArray();
        if (m != null)
        {
            var all = await _engine.Memories.GetRecentAsync(5000);
            var derived = all.Where(x => x.IsDerived && x.SourceMemoryIds != null
                && x.SourceMemoryIds.Contains(m.Id.ToString())).ToList();
            foreach (var d in derived)
            {
                arr.Add(new JsonObject
                {
                    ["id"] = d.Id,
                    ["type"] = d.Type,
                    ["subject"] = d.Subject ?? "",
                    ["content"] = d.Content.Length <= 120 ? d.Content : d.Content[..120] + "...",
                    ["_link"] = $"/p/memory/{d.Id}"
                });
            }
        }
        return new DataResult { Data = arr };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class MemoryDetailLinksSource : IDataSource
{
    private readonly MasterEngine _engine;
    public MemoryDetailLinksSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var m = await MemoryDetailHelper.GetMemoryAsync(_engine, query);
        var arr = new JsonArray();
        if (m != null)
        {
            var links = await _engine.MemoryLinks.GetByMemoryIdAsync(m.Id);
            foreach (var link in links.OrderByDescending(l => l.Relevance))
            {
                int otherId = link.SourceId == m.Id ? link.TargetId : link.SourceId;
                var other = await _engine.Memories.GetByIdAsync(otherId);
                if (other != null)
                {
                    arr.Add(new JsonObject
                    {
                        ["id"] = other.Id,
                        ["type"] = other.Type,
                        ["link_type"] = link.LinkType,
                        ["relevance"] = link.Relevance.ToString("F2"),
                        ["content"] = other.Content.Length <= 120 ? other.Content : other.Content[..120] + "...",
                        ["_link"] = $"/p/memory/{other.Id}"
                    });
                }
            }
        }
        return new DataResult { Data = arr };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}


// ================ 临时库数据源 ================

internal class TempMemoryStatsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public TempMemoryStatsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = await _engine.TempMemories.GetAllAsync();
        var high = all.Count(t => t.Confidence == "high");
        var low = all.Count(t => t.Confidence == "low");
        return new DataResult
        {
            Data = new JsonObject
            {
                ["total"] = all.Count.ToString(),
                ["high_conf"] = high.ToString(),
                ["low_conf"] = low.ToString()
            }
        };
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action == "clear")
        {
            await _engine.TempMemories.ClearAllAsync();
            return new ActionResult { Success = true, Message = "已清空全部临时记忆" };
        }
        return new ActionResult { Success = false, Message = $"未知操作: {action}" };
    }
}

internal class TempMemorySource : IDataSource
{
    private readonly MasterEngine _engine;
    public TempMemorySource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = await _engine.TempMemories.GetAllAsync();
        var filtered = Filter(all, query);
        var total = filtered.Count;

        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 25;
        var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var arr = new JsonArray();
        foreach (var t in paged)
        {
            arr.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["type"] = t.Type,
                ["subject"] = t.Subject ?? "",
                ["content"] = t.Content,
                ["certainty"] = t.Confidence,
                ["person_id"] = t.PersonId?.ToString() ?? "—",
                ["channel_id"] = t.ChannelId?.ToString() ?? "—",
                ["created"] = t.CreatedAt.ToString("MM-dd HH:mm")
            });
        }
        return new DataResult { Data = arr, TotalCount = total };
    }

    private static List<TempMemoryEntry> Filter(List<TempMemoryEntry> all, DataQuery? query)
    {
        var result = all.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var kw = query.Search.Trim();
            result = result.Where(t => t.Content.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        if (query?.Filters != null)
        {
            foreach (var f in query.Filters)
            {
                if (string.IsNullOrEmpty(f.Value)) continue;
                switch (f.Field)
                {
                    case "type":
                        result = result.Where(t => t.Type == f.Value);
                        break;
                    case "certainty":
                        result = result.Where(t => t.Confidence == f.Value);
                        break;
                }
            }
        }

        return result.OrderByDescending(t => t.CreatedAt).ToList();
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action == "delete")
        {
            var id = data?["id"]?.GetValue<int>() ?? 0;
            if (id > 0)
            {
                var all = await _engine.TempMemories.GetAllAsync();
                var temp = all.FirstOrDefault(t => t.Id == id);
                if (temp != null)
                {
                    await _engine.TempMemories.DeleteAsync(temp);
                    return new ActionResult { Success = true, Message = "已删除" };
                }
            }
            return new ActionResult { Success = false, Message = "未找到" };
        }
        return new ActionResult { Success = false, Message = $"未知操作: {action}" };
    }
}


// ================ 人设记忆数据源 ================

internal class PersonaFormSource : IDataSource
{
    private readonly MasterEngine _engine;
    public PersonaFormSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
        => Task.FromResult(new DataResult { Data = new JsonObject() });

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action == "save")
        {
            var content = data?["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(content))
                return new ActionResult { Success = false, Message = "内容不能为空" };
            var category = data?["category"]?.ToString();
            if (string.IsNullOrWhiteSpace(category)) category = null;

            byte[]? emb = null;
            try
            {
                var vec = await _engine.Embedding.GetEmbeddingAsync(content);
                emb = Client.SiliconFlowEmbeddingProvider.FloatsToBytes(vec);
            }
            catch { Signal.Warn(LogGroup.Memory, "人设记忆embedding失败", new { content_length = content.Length }); }

            await _engine.PersonaMemories.CreateAsync(content, emb, category);
            return new ActionResult { Success = true, Message = "人设记忆已添加" };
        }
        return new ActionResult { Success = false, Message = $"未知操作: {action}" };
    }
}

internal class PersonaStatsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public PersonaStatsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = await _engine.PersonaMemories.GetAllAsync();
        var catDist = all.GroupBy(p => string.IsNullOrEmpty(p.Category) ? "(未分类)" : p.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();

        return new DataResult
        {
            Data = new JsonObject
            {
                ["total"] = all.Count.ToString(),
                ["categories"] = string.Join("\n", catDist)
            }
        };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class PersonaTableSource : IDataSource
{
    private readonly MasterEngine _engine;
    public PersonaTableSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = await _engine.PersonaMemories.GetAllAsync();
        var filtered = all.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var kw = query.Search.Trim();
            filtered = filtered.Where(p =>
                p.Content.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (p.Category != null && p.Category.Contains(kw, StringComparison.OrdinalIgnoreCase)));
        }

        var list = filtered.OrderByDescending(p => p.CreatedAt).ToList();
        var total = list.Count;

        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 25;
        var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var arr = new JsonArray();
        foreach (var p in paged)
        {
            arr.Add(new JsonObject
            {
                ["id"] = p.Id,
                ["category"] = string.IsNullOrEmpty(p.Category) ? "未分类" : p.Category,
                ["content"] = p.Content,
                ["created"] = p.CreatedAt.ToString("MM-dd HH:mm")
            });
        }
        return new DataResult { Data = arr, TotalCount = total };
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action == "edit")
        {
            var editId = data?["id"]?.GetValue<int>() ?? 0;
            var editContent = data?["content"]?.ToString();
            var editCategory = data?["category"]?.ToString();
            var all = await _engine.PersonaMemories.GetAllAsync();
            var entry = all.FirstOrDefault(p => p.Id == editId);
            if (entry == null)
                return new ActionResult { Success = false, Message = "未找到" };
            if (!string.IsNullOrWhiteSpace(editContent)) entry.Content = editContent;
            if (editCategory != null) entry.Category = string.IsNullOrWhiteSpace(editCategory) ? null : editCategory;
            await _engine.PersonaMemories.UpdateAsync(entry);
            return new ActionResult { Success = true, Message = "已更新" };
        }

        if (action == "delete")
        {
            var delId = data?["id"]?.GetValue<int>() ?? 0;
            var all = await _engine.PersonaMemories.GetAllAsync();
            var delEntry = all.FirstOrDefault(p => p.Id == delId);
            if (delEntry != null)
            {
                await _engine.PersonaMemories.DeleteAsync(delEntry);
                return new ActionResult { Success = true, Message = "已删除" };
            }
            return new ActionResult { Success = false, Message = "未找到" };
        }

        return new ActionResult { Success = false, Message = $"未知操作: {action}" };
    }
}

// ================ 自然人管理数据源 ================

internal class PeopleListSource : IDataSource
{
    private readonly MasterEngine _engine;
    public PeopleListSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var persons = await _engine.Session.GetAllPersonsAsync();
        var filtered = persons.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var kw = query.Search.Trim().ToLowerInvariant();
            filtered = filtered.Where(p =>
                p.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (p.Aliases?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.FastMemory?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = filtered.OrderBy(p => p.Id).ToList();
        var arr = new JsonArray();
        foreach (var p in list)
        {
            arr.Add(new JsonObject
            {
                ["id"] = p.Id,
                ["name"] = string.IsNullOrEmpty(p.Name) ? $"(无名称 #{p.Id})" : p.Name,
                ["trustLevel"] = p.TrustLevel.ToString(),
                ["trustProgress"] = p.TrustProgress,
                ["alertLevel"] = p.AlertLevel.ToString(),
                ["fastMemory"] = string.IsNullOrEmpty(p.FastMemory) ? "—" : (p.FastMemory.Length > 60 ? p.FastMemory[..60] + "..." : p.FastMemory),
            });
        }
        return new DataResult { Data = arr, TotalCount = arr.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false, Message = "不支持" });
}

internal class PeopleEditSource : IDataSource
{
    private readonly MasterEngine _engine;
    private int _selectedId;
    public PeopleEditSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        if (query?.Extra is JsonObject extra)
            _selectedId = (int?)extra["id"] ?? _selectedId;

        if (_selectedId == 0)
            return new DataResult { Data = new JsonObject() };

        var person = await _engine.Session.GetPersonByIdAsync(_selectedId);
        if (person == null)
            return new DataResult { Data = new JsonObject() };

        var users = await _engine.Session.GetUsersByPersonIdAsync(_selectedId);
        var accountsStr = users.Count == 0 ? "" : string.Join("\n",
            users.Select(u => $"{u.Platform}: {u.PlatformId}" + (string.IsNullOrEmpty(u.DisplayName) ? "" : $" ({u.DisplayName})")));

        return new DataResult
        {
            Data = new JsonObject
            {
                ["id"] = person.Id,
                ["name"] = person.Name ?? "",
                ["aliases"] = person.Aliases ?? "",
                ["trustLevel"] = ((int)person.TrustLevel).ToString(),
                ["trustProgress"] = person.TrustProgress,
                ["alertLevel"] = person.AlertLevel.ToString(),
                ["fastMemory"] = person.FastMemory ?? "",
                ["accounts"] = accountsStr,
            }
        };
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action != "save" || _selectedId == 0 || data is not JsonObject obj)
            return new ActionResult { Success = false, Message = "无效请求" };

        var person = await _engine.Session.GetPersonByIdAsync(_selectedId);
        if (person == null)
            return new ActionResult { Success = false, Message = "自然人不存在" };

        if (obj.TryGetPropertyValue("name", out var nameNode))
            person.Name = nameNode?.ToString() ?? "";
        if (obj.TryGetPropertyValue("aliases", out var aliasesNode))
            person.Aliases = aliasesNode?.ToString() ?? "";
        if (obj.TryGetPropertyValue("trustLevel", out var tlNode) && int.TryParse(tlNode?.ToString(), out var tlVal) && Enum.IsDefined(typeof(TrustLevel), tlVal))
            person.TrustLevel = (TrustLevel)tlVal;
        if (obj.TryGetPropertyValue("trustProgress", out var tpNode) && float.TryParse(tpNode?.ToString(), out var tpVal))
            person.TrustProgress = tpVal;
        if (obj.TryGetPropertyValue("alertLevel", out var alNode) && int.TryParse(alNode?.ToString(), out var alVal) && alVal >= 0 && alVal <= 4)
            person.AlertLevel = alVal;
        if (obj.TryGetPropertyValue("fastMemory", out var fmNode))
            person.FastMemory = fmNode?.ToString() ?? "";

        await _engine.Session.UpdatePersonAsync(person);
        return new ActionResult { Success = true, Message = "已保存" };
    }
}

internal class MemoryGraphRedirectSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        return Task.FromResult(new DataResult
        {
            Data = new JsonObject(),
            Meta = new JsonObject { ["redirect"] = "/memory/graph" }
        });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });
}
