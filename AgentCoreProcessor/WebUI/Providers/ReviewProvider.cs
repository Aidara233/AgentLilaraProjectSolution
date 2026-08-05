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
/// 复盘 Provider：读取新验证系统数据（ReviewQueueItems + ConflictGroups，由 MindEngine 服务写入）。
/// 旧 ReviewEngine / ReviewLogs / EvaluationScores 已整体移除，不再引用。
/// </summary>
[WebUIProvider(BuiltIn = true)]
internal class ReviewProvider : IWebUIProvider
{
    public string Id => "core-review";
    public string DisplayName => "复盘";

    private readonly MasterEngine _engine;

    public ReviewProvider(MasterEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<PageDefinition> Pages => new List<PageDefinition>
    {
        BuildQueuePage(),
        BuildGroupsPage()
    };

    // ======== 待验证队列 /p/review ========

    private PageDefinition BuildQueuePage() => new()
    {
        Route = "review",
        Meta = new PageMeta { Title = "待验证队列", Icon = "bi-list-check", Group = "复盘", Order = 60 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "review-queue", Type = CardType.Table, DataSourceId = "review-queue", Title = "待验证队列",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "50px", Sortable = false },
                        new() { Field = "target", Header = "目标引用", Width = "140px" },
                        new() { Field = "kind", Header = "类型", Width = "110px", Format = ColumnFormat.Badge },
                        new() { Field = "priority", Header = "优先级", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "source", Header = "来源", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "status", Header = "状态", Width = "100px", Format = ColumnFormat.Badge },
                        new() { Field = "reason", Header = "原因" },
                        new() { Field = "created", Header = "创建时间", Width = "130px" }
                    },
                    Filters = new()
                    {
                        new() { Field = "status", Label = "全部状态", Options = new()
                        {
                            new() { Value = "Pending", Label = "待处理" },
                            new() { Value = "UnderReview", Label = "处理中" },
                            new() { Value = "Done", Label = "已完成" },
                            new() { Value = "Dropped", Label = "已丢弃" }
                        }},
                        new() { Field = "priority", Label = "全部优先级", Options = new()
                        {
                            new() { Value = "High", Label = "高" },
                            new() { Value = "Normal", Label = "中" },
                            new() { Value = "Low", Label = "低" }
                        }}
                    },
                    Searchable = true,
                    DefaultPageSize = 20
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "review-queue", Source = new ReviewQueueSource(_engine) }
        }
    };

    // ======== 冲突组 /p/review-groups ========

    private PageDefinition BuildGroupsPage() => new()
    {
        Route = "review-groups",
        Meta = new PageMeta { Title = "冲突组", Icon = "bi-diagram-3", Group = "复盘", Order = 61 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "conflict-groups", Type = CardType.Table, DataSourceId = "conflict-groups", Title = "冲突组",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "50px", Sortable = false },
                        new() { Field = "subject", Header = "主体", Width = "140px" },
                        new() { Field = "predicate", Header = "属性", Width = "140px" },
                        new() { Field = "reason", Header = "原因" },
                        new() { Field = "detected", Header = "检测时间", Width = "130px" },
                        new() { Field = "resolved", Header = "解决时间", Width = "130px" },
                        new() { Field = "member_count", Header = "成员数", Width = "70px" }
                    },
                    Filters = new()
                    {
                        new() { Field = "resolution", Label = "全部", Options = new()
                        {
                            new() { Value = "active", Label = "未解决" },
                            new() { Value = "resolved", Label = "已解决" }
                        }}
                    },
                    Searchable = true,
                    DefaultPageSize = 20
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "conflict-groups", Source = new ConflictGroupsSource(_engine) }
        }
    };
}

// ======== 数据源 ========

/// <summary>
/// 待验证队列数据源。读 ReviewQueueItems 全量，支持 Status/Priority 过滤 + 搜索 + 分页。
/// 入队/状态流转由 MindEngine.Queue（ReviewQueueService）负责，这里只读展示。
/// </summary>
internal class ReviewQueueSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ReviewQueueSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = await _engine.ReviewQueue.GetAllAsync();
        var filtered = Filter(all, query);

        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 20;
        var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize);

        var arr = new JsonArray();
        foreach (var item in paged)
        {
            arr.Add(new JsonObject
            {
                ["id"] = item.Id.ToString(),
                ["target"] = item.TargetRef,
                ["kind"] = item.TargetKind,
                ["priority"] = item.Priority,
                ["source"] = item.Source,
                ["status"] = item.Status,
                ["reason"] = item.Reason ?? "—",
                ["created"] = item.CreatedAt.ToString("MM-dd HH:mm")
            });
        }
        return new DataResult { Data = arr, TotalCount = filtered.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });

    private static List<ReviewQueueItem> Filter(List<ReviewQueueItem> all, DataQuery? query)
    {
        var result = all.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var kw = query.Search.Trim();
            result = result.Where(i =>
                i.TargetRef.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                i.TargetKind.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                i.Source.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (i.Reason?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (query?.Filters != null)
        {
            foreach (var f in query.Filters)
            {
                if (string.IsNullOrEmpty(f.Value)) continue;
                switch (f.Field)
                {
                    case "status":
                        result = result.Where(i => i.Status == f.Value);
                        break;
                    case "priority":
                        result = result.Where(i => i.Priority == f.Value);
                        break;
                }
            }
        }

        return result.OrderByDescending(i => i.CreatedAt).ToList();
    }
}

/// <summary>
/// 冲突组数据源。读 ConflictGroups 全量 + 逐组统计成员数（GetMembersAsync），
/// 支持未解决/已解决过滤 + 搜索 + 分页。
/// </summary>
internal class ConflictGroupsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ConflictGroupsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = await _engine.ConflictGroups.GetAllAsync();
        var filtered = Filter(all, query);

        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 20;
        var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize);

        var arr = new JsonArray();
        foreach (var g in paged)
        {
            var members = await _engine.ConflictGroups.GetMembersAsync(g.Id);
            arr.Add(new JsonObject
            {
                ["id"] = g.Id.ToString(),
                ["subject"] = g.SubjectKey,
                ["predicate"] = g.PredicateKey,
                ["reason"] = g.Reason ?? "—",
                ["detected"] = g.DetectedAt.ToString("MM-dd HH:mm"),
                ["resolved"] = g.ResolvedAt?.ToString("MM-dd HH:mm") ?? "—",
                ["member_count"] = members.Count.ToString()
            });
        }
        return new DataResult { Data = arr, TotalCount = filtered.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });

    private static List<ConflictGroup> Filter(List<ConflictGroup> all, DataQuery? query)
    {
        var result = all.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var kw = query.Search.Trim();
            result = result.Where(g =>
                g.SubjectKey.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                g.PredicateKey.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (g.Reason?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (query?.Filters != null)
        {
            foreach (var f in query.Filters)
            {
                if (string.IsNullOrEmpty(f.Value)) continue;
                switch (f.Field)
                {
                    case "resolution":
                        if (f.Value == "active")
                            result = result.Where(g => g.ResolvedAt == null);
                        else if (f.Value == "resolved")
                            result = result.Where(g => g.ResolvedAt != null);
                        break;
                }
            }
        }

        return result.OrderByDescending(g => g.DetectedAt).ToList();
    }
}
