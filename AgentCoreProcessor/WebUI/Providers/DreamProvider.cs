using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

/// <summary>
/// 心智维护数据提供器（原"做梦"引擎页）。
/// 旧 DreamEngine / TempMemories 已废弃，改为读新心智数据：
/// - 框架事件（FrameworkEvents）：L0/L1 运行标记
/// - 推断断言（Claims IsInference=true）：模型推断出的三元组
/// </summary>
[WebUIProvider(BuiltIn = true)]
internal class DreamProvider : IWebUIProvider
{
    public string Id => "core-dream";
    public string DisplayName => "做梦";

    private readonly MasterEngine _engine;

    public DreamProvider(MasterEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<PageDefinition> Pages => new List<PageDefinition>
    {
        BuildEventsPage(),
        BuildClaimsPage()
    };

    // ---- 框架事件页 ----

    private PageDefinition BuildEventsPage() => new()
    {
        Route = "dream",
        Meta = new PageMeta { Title = "框架事件", Icon = "bi-moon-stars", Group = "心智维护", Order = 50 },
        LayoutType = PageLayoutType.Sidebar,
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "dream-events", Type = CardType.Table, DataSourceId = "dream-events", Title = "框架事件",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "level", Header = "层级", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "type", Header = "类型", Width = "180px" },
                        new() { Field = "detail", Header = "详情" },
                        new() { Field = "timestamp", Header = "时间", Width = "160px", Format = ColumnFormat.DateTime }
                    },
                    Filters = new()
                    {
                        new() { Field = "level", Label = "全部层级", Options = new()
                        {
                            new() { Value = "L0", Label = "L0（机械日志）" },
                            new() { Value = "L1", Label = "L1（重要标记）" }
                        }},
                    },
                    Searchable = true,
                    Paginated = true,
                    DefaultPageSize = 30
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "dream-events", Source = new DreamEventsSource(_engine) }
        }
    };

    // ---- 推断断言页 ----

    private PageDefinition BuildClaimsPage() => new()
    {
        Route = "dream-claims",
        Meta = new PageMeta { Title = "推断断言", Icon = "bi-lightbulb", Group = "心智维护", Order = 51 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "dream-claims", Type = CardType.Table, DataSourceId = "dream-claims", Title = "推断断言",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "raw", Header = "断言原文" },
                        new() { Field = "credibility", Header = "可信度", Width = "100px" },
                        new() { Field = "recordedAt", Header = "记录时间", Width = "160px", Format = ColumnFormat.DateTime }
                    },
                    Searchable = true,
                    Paginated = true,
                    DefaultPageSize = 30
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "dream-claims", Source = new DreamClaimsSource(_engine) }
        }
    };
}

// ======== 数据源 ========

/// <summary>
/// 框架事件列表数据源。按 Level 过滤时走 GetByLevelAsync，否则取最近 N 条。
/// </summary>
internal class DreamEventsSource : IDataSource
{
    private readonly MasterEngine _engine;
    /// <summary>一次拉取上限（倒序取最近 N 条）。</summary>
    private const int FetchLimit = 500;

    public DreamEventsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        // 按层级过滤：有明确层级时用 GetByLevelAsync，否则 GetRecentAsync
        var level = query?.Filters?.FirstOrDefault(f => f.Field == "level")?.Value;
        var events = !string.IsNullOrWhiteSpace(level) && level != "all"
            ? await _engine.FrameworkEvents.GetByLevelAsync(level, FetchLimit)
            : await _engine.FrameworkEvents.GetRecentAsync(FetchLimit);

        // 搜索：匹配类型或详情文本
        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var kw = query.Search.Trim();
            events = events.Where(e =>
                (e.Type ?? "").Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (e.DetailJson ?? "").Contains(kw, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        var total = events.Count;

        // 分页
        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 30;
        var paged = events.Skip((page - 1) * pageSize).Take(pageSize);

        var arr = new JsonArray();
        foreach (var e in paged)
        {
            arr.Add(new JsonObject
            {
                ["id"] = e.Id,
                ["level"] = e.Level,
                ["type"] = e.Type,
                ["detail"] = string.IsNullOrEmpty(e.DetailJson) ? "—" : e.DetailJson,
                ["timestamp"] = e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        return new DataResult { Data = arr, TotalCount = total };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

/// <summary>
/// 推断断言列表数据源。筛选 IsInference=true 的 Claims（Raw + Credibility）。
/// Claims 仓库暂无 IsInference 专用查询，先全量取回后内存过滤（简化版）。
/// </summary>
internal class DreamClaimsSource : IDataSource
{
    private readonly MasterEngine _engine;

    public DreamClaimsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = await _engine.Claims.GetAllAsync();
        var claims = all.Where(c => c.IsInference).ToList();

        // 搜索：匹配断言原文
        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var kw = query.Search.Trim();
            claims = claims.Where(c => c.Raw.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var total = claims.Count;

        // 分页
        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 30;
        var paged = claims.Skip((page - 1) * pageSize).Take(pageSize);

        var arr = new JsonArray();
        foreach (var c in paged)
        {
            arr.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["raw"] = string.IsNullOrEmpty(c.Raw) ? "—" : c.Raw,
                ["credibility"] = c.Credibility.ToString("F2"),
                ["recordedAt"] = c.RecordedAt.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        return new DataResult { Data = arr, TotalCount = total };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}
