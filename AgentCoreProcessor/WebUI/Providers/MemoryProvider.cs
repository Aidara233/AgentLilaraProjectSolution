using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.WebUI;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.WebUI.Providers;

/// <summary>
/// 记忆管理数据提供器（新心智数据版）。
/// 旧记忆仓库已废弃，全部改为读取新心智引擎数据：
/// - Claims（断言）：主库浏览页
/// - Journals（经历）：经历页
/// - Keywords（关键词）：关键词页
/// - EmotionSnapshots（情绪快照）：情绪页
/// </summary>
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
        BuildJournalsPage(),
        BuildKeywordsPage(),
        BuildEmotionPage(),
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
                        new() { Field = "claims_total", Label = "断言总数" },
                        new() { Field = "journals_total", Label = "经历总数" },
                        new() { Field = "claim_type_dist", Label = "断言类型", IsMultiline = true },
                        new() { Field = "claim_status_dist", Label = "处理状态", IsMultiline = true },
                        new() { Field = "claim_source_dist", Label = "来源分布", IsMultiline = true }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "main-table", Type = CardType.Table, DataSourceId = "main-table", Title = "断言列表",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "60px" },
                        new() { Field = "_inference", Header = "类型", Width = "70px", Format = ColumnFormat.Badge },
                        new() { Field = "subject", Header = "主体", Width = "110px" },
                        new() { Field = "predicate", Header = "谓词", Width = "100px" },
                        new() { Field = "object", Header = "客体", Width = "110px" },
                        new() { Field = "raw", Header = "原文" },
                        new() { Field = "credibility", Header = "可信度", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "status", Header = "状态", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "occurred", Header = "发生", Width = "110px" }
                    },
                    RowKeyField = "id",
                    DefaultPageSize = 25,
                    Filters = new()
                    {
                        new() { Field = "inference", Label = "全部类型", Options = new()
                        {
                            new() { Value = "inference", Label = "推断" },
                            new() { Value = "stated", Label = "陈述" }
                        }},
                        new() { Field = "status", Label = "全部状态", Options = new()
                        {
                            new() { Value = "Raw", Label = "Raw" },
                            new() { Value = "Tagged", Label = "Tagged" },
                            new() { Value = "Processed", Label = "Processed" },
                            new() { Value = "Archived", Label = "Archived" }
                        }},
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "main-stats", Factory = () => new MemoryStatsSource(_engine) },
            new() { Id = "main-table", Factory = () => new ClaimsSource(_engine), Paging = DataPagingMode.Server }
        }
    };

    // ================ 经历 ================

    private PageDefinition BuildJournalsPage() => new()
    {
        Route = "memory-journals",
        Meta = new PageMeta { Title = "经历", Icon = "bi-journal-text", Group = "记忆", Order = 81 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "journals-table", Type = CardType.Table, DataSourceId = "journals-table", Title = "经历列表",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "60px" },
                        new() { Field = "kind", Header = "类型", Width = "100px", Format = ColumnFormat.Badge },
                        new() { Field = "source", Header = "来源", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "content", Header = "内容" },
                        new() { Field = "channel_id", Header = "频道", Width = "70px" },
                        new() { Field = "session_id", Header = "会话", Width = "70px" },
                        new() { Field = "occurred", Header = "发生时间", Width = "110px" }
                    },
                    DefaultPageSize = 25,
                    Filters = new()
                    {
                        new() { Field = "kind", Label = "全部类型", Options = new()
                        {
                            new() { Value = "Experience", Label = "Experience (L2)" },
                            new() { Value = "Reflection", Label = "Reflection (L3)" }
                        }},
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "journals-table", Factory = () => new JournalsSource(_engine), Paging = DataPagingMode.Server }
        }
    };

    // ================ 关键词 ================

    private PageDefinition BuildKeywordsPage() => new()
    {
        Route = "memory-keywords",
        Meta = new PageMeta { Title = "关键词", Icon = "bi-tags", Group = "记忆", Order = 82 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "keywords-table", Type = CardType.Table, DataSourceId = "keywords-table", Title = "关键词库",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "60px" },
                        new() { Field = "name", Header = "名称" },
                        new() { Field = "role", Header = "角色", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "category", Header = "分类", Width = "100px" },
                        new() { Field = "_exclusive", Header = "排他", Width = "60px", Format = ColumnFormat.Badge },
                        new() { Field = "use_count", Header = "使用次数", Width = "90px" },
                        new() { Field = "last_used", Header = "上次使用", Width = "110px" }
                    },
                    DefaultPageSize = 25,
                    Filters = new()
                    {
                        new() { Field = "role", Label = "全部角色", Options = new()
                        {
                            new() { Value = "attribute", Label = "attribute" },
                            new() { Value = "keyword", Label = "keyword" }
                        }},
                        new() { Field = "exclusive", Label = "全部排他", Options = new()
                        {
                            new() { Value = "1", Label = "排他" },
                            new() { Value = "0", Label = "非排他" }
                        }},
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "keywords-table", Factory = () => new KeywordsSource(_engine), Paging = DataPagingMode.Server }
        }
    };

    // ================ 情绪快照 ================

    private PageDefinition BuildEmotionPage() => new()
    {
        Route = "memory-emotion",
        Meta = new PageMeta { Title = "情绪快照", Icon = "bi-activity", Group = "记忆", Order = 83 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "emotion-latest", Type = CardType.Status, DataSourceId = "emotion-latest", Title = "最新情绪",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "excitement", Label = "兴奋" },
                        new() { Field = "joy", Label = "喜悦" },
                        new() { Field = "alertness", Label = "敏觉" },
                        new() { Field = "satisfaction", Label = "满足" },
                        new() { Field = "optimism", Label = "积极" },
                        new() { Field = "created", Label = "快照时间" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "emotion-table", Type = CardType.Table, DataSourceId = "emotion-table", Title = "最近快照",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "id", Header = "ID", Width = "60px" },
                        new() { Field = "excitement", Header = "兴奋", Width = "80px" },
                        new() { Field = "joy", Header = "喜悦", Width = "80px" },
                        new() { Field = "alertness", Header = "敏觉", Width = "80px" },
                        new() { Field = "satisfaction", Header = "满足", Width = "80px" },
                        new() { Field = "optimism", Header = "积极", Width = "80px" },
                        new() { Field = "created", Header = "创建时间", Width = "130px" }
                    },
                    DefaultPageSize = 25,
                    Paginated = false,
                    Searchable = false
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "emotion-latest", Factory = () => new EmotionLatestSource(_engine) },
            new() { Id = "emotion-table", Factory = () => new EmotionSource(_engine), Paging = DataPagingMode.Client }
        }
    };
}


// ================ 主库数据源 ================

/// <summary>
/// 主库统计：断言/经历总数 + 断言类型分布（陈述 vs 推断）+ 处理状态分布 + 来源分布。
/// </summary>
internal class MemoryStatsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public MemoryStatsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var claims = await _engine.Claims.GetAllAsync();
        var journals = await _engine.Journals.GetAllAsync();

        // 断言类型分布（陈述 vs 推断）
        var typeDist = claims.GroupBy(c => c.IsInference ? "推断" : "陈述")
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();

        // 处理状态分布
        var statusDist = claims.GroupBy(c => string.IsNullOrEmpty(c.Status) ? "(未知)" : c.Status)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();

        // 来源分布（解析 SourceJson.who）
        var sourceDist = claims.GroupBy(c => ParseSourceWho(c.SourceJson))
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();

        var data = new JsonObject
        {
            ["claims_total"] = claims.Count.ToString(),
            ["journals_total"] = journals.Count.ToString(),
            ["claim_type_dist"] = typeDist.Count > 0 ? string.Join("\n", typeDist) : "（无数据）",
            ["claim_status_dist"] = statusDist.Count > 0 ? string.Join("\n", statusDist) : "（无数据）",
            ["claim_source_dist"] = sourceDist.Count > 0 ? string.Join("\n", sourceDist) : "（无数据）"
        };
        return new DataResult { Data = data };
    }

    /// <summary>从断言来源 JSON 中提取 who 字段。</summary>
    private static string ParseSourceWho(string? sourceJson)
    {
        if (string.IsNullOrWhiteSpace(sourceJson) || sourceJson == "{}") return "未知";
        try
        {
            var who = JObject.Parse(sourceJson)["who"]?.ToString();
            return string.IsNullOrEmpty(who) ? "未知" : who;
        }
        catch { return "未知"; }
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

/// <summary>
/// 断言列表（全量缓存 + 内存过滤分页）。展示 Raw + Credibility + IsInference 标记 + 槽位表面词。
/// Claims 仓库无 GetRecentAsync，这里 GetAllAsync 后按 RecordedAt 倒序。
/// </summary>
internal class ClaimsSource : IDataSource
{
    private readonly MasterEngine _engine;
    private List<Claim> _allCache = new();
    private DateTime _cacheTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);

    public ClaimsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        // 缓存全量数据减少 DB 查询
        if ((DateTime.Now - _cacheTime) > CacheTtl)
        {
            _allCache = await _engine.Claims.GetAllAsync();
            _cacheTime = DateTime.Now;
        }

        var filtered = Filter(_allCache, query).ToList();
        var total = filtered.Count;

        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 25;
        var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var arr = new JsonArray();
        foreach (var c in paged)
        {
            arr.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["inference"] = c.IsInference ? "推断" : "陈述",
                ["_inference"] = c.IsInference ? "inference" : "stated",
                ["subject"] = ClaimSlot.FromJson(c.SubjectJson)?.Index ?? "—",
                ["predicate"] = ClaimSlot.FromJson(c.PredicateJson)?.Index ?? "—",
                ["object"] = ClaimSlot.FromJson(c.ObjectJson)?.Index ?? "—",
                ["raw"] = Truncate(c.Raw, 150),
                ["credibility"] = c.Credibility.ToString("F2"),
                ["status"] = string.IsNullOrEmpty(c.Status) ? "—" : c.Status,
                ["occurred"] = c.OccurredAt is DateTime occ
                    ? AgentCoreProcessor.Util.TimeUtil.ToLocalSafe(occ).ToString("MM-dd HH:mm")
                    : "—"
            });
        }
        return new DataResult { Data = arr, TotalCount = total };
    }

    private static IEnumerable<Claim> Filter(IEnumerable<Claim> all, DataQuery? query)
    {
        var result = all;

        // 搜索：匹配原文或槽位表面词
        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var kw = query.Search.Trim();
            result = result.Where(c =>
                c.Raw.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (ClaimSlot.FromJson(c.SubjectJson)?.Index ?? "").Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (ClaimSlot.FromJson(c.PredicateJson)?.Index ?? "").Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (ClaimSlot.FromJson(c.ObjectJson)?.Index ?? "").Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        // 过滤：类型（推断/陈述）+ 处理状态
        if (query?.Filters != null)
        {
            foreach (var f in query.Filters)
            {
                if (string.IsNullOrEmpty(f.Value)) continue;
                switch (f.Field)
                {
                    case "inference":
                        var isInfer = f.Value == "inference";
                        result = result.Where(c => c.IsInference == isInfer);
                        break;
                    case "status":
                        result = result.Where(c => c.Status == f.Value);
                        break;
                }
            }
        }

        return result.OrderByDescending(c => c.RecordedAt);
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "—" : (s.Length <= max ? s : s[..max] + "...");

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}


// ================ 经历数据源 ================

/// <summary>
/// 经历列表。按 Kind 过滤时走 GetByKindAsync，否则全量取回后内存过滤。
/// </summary>
internal class JournalsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public JournalsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        // 有 Kind 过滤时走仓库优化查询，否则全量
        var kind = MemoryQueryHelper.GetFilterValue(query, "kind");
        var all = !string.IsNullOrEmpty(kind)
            ? await _engine.Journals.GetByKindAsync(kind, 100000)
            : await _engine.Journals.GetAllAsync();

        var filtered = Filter(all, query).ToList();
        var total = filtered.Count;

        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 25;
        var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var arr = new JsonArray();
        foreach (var j in paged)
        {
            arr.Add(new JsonObject
            {
                ["id"] = j.Id,
                ["kind"] = j.Kind,
                ["source"] = j.Source,
                ["content"] = Truncate(j.Content, 200),
                ["channel_id"] = j.ChannelId?.ToString() ?? "—",
                ["session_id"] = j.SessionId?.ToString() ?? "—",
                ["occurred"] = AgentCoreProcessor.Util.TimeUtil.ToLocalSafe(j.OccurredAt).ToString("MM-dd HH:mm")
            });
        }
        return new DataResult { Data = arr, TotalCount = total };
    }

    private static IEnumerable<Journal> Filter(IEnumerable<Journal> all, DataQuery? query)
    {
        var result = all;

        // 搜索：匹配正文
        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var kw = query.Search.Trim();
            result = result.Where(j => j.Content.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        // 过滤：经历类型（已在仓库层优化，这里兜底再筛一次）
        var kind = MemoryQueryHelper.GetFilterValue(query, "kind");
        if (!string.IsNullOrEmpty(kind))
            result = result.Where(j => j.Kind == kind);

        return result.OrderByDescending(j => j.OccurredAt);
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "—" : (s.Length <= max ? s : s[..max] + "...");

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}


// ================ 关键词数据源 ================

/// <summary>
/// 关键词库列表。展示 Name + Role + IsExclusive + UseCount，支持按角色/排他过滤。
/// </summary>
internal class KeywordsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public KeywordsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = await _engine.Keywords.GetAllAsync();
        var filtered = Filter(all, query).ToList();
        var total = filtered.Count;

        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 25;
        var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var arr = new JsonArray();
        foreach (var k in paged)
        {
            arr.Add(new JsonObject
            {
                ["id"] = k.Id,
                ["name"] = k.Name,
                ["role"] = k.Role,
                ["category"] = string.IsNullOrEmpty(k.Category) ? "—" : k.Category,
                ["is_exclusive"] = k.IsExclusive ? "是" : "否",
                ["_exclusive"] = k.IsExclusive ? "1" : "0",
                ["use_count"] = k.UseCount,
                ["last_used"] = k.LastUsedAt is DateTime lu
                    ? AgentCoreProcessor.Util.TimeUtil.ToLocalSafe(lu).ToString("MM-dd HH:mm")
                    : "—"
            });
        }
        return new DataResult { Data = arr, TotalCount = total };
    }

    private static IEnumerable<Keyword> Filter(IEnumerable<Keyword> all, DataQuery? query)
    {
        var result = all;

        // 搜索：匹配名称或别名
        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var kw = query.Search.Trim();
            result = result.Where(k =>
                k.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (k.AliasesJson ?? "").Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        // 过滤：角色 + 排他标记
        if (query?.Filters != null)
        {
            foreach (var f in query.Filters)
            {
                if (string.IsNullOrEmpty(f.Value)) continue;
                switch (f.Field)
                {
                    case "role":
                        result = result.Where(k => k.Role == f.Value);
                        break;
                    case "exclusive":
                        var isEx = f.Value == "1";
                        result = result.Where(k => k.IsExclusive == isEx);
                        break;
                }
            }
        }

        return result.OrderByDescending(k => k.UseCount).ThenBy(k => k.Name);
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}


// ================ 情绪快照数据源 ================

/// <summary>
/// 最新一条情绪快照（五轴值 + 时间），用于状态卡展示。
/// </summary>
internal class EmotionLatestSource : IDataSource
{
    private readonly MasterEngine _engine;
    public EmotionLatestSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var latest = await _engine.EmotionSnapshots.GetLatestAsync();
        if (latest == null)
            return new DataResult { Data = new JsonObject { ["excitement"] = "（暂无快照）" } };

        var data = new JsonObject
        {
            ["excitement"] = latest.Excitement.ToString("F2"),
            ["joy"] = latest.Joy.ToString("F2"),
            ["alertness"] = latest.Alertness.ToString("F2"),
            ["satisfaction"] = latest.Satisfaction.ToString("F2"),
            ["optimism"] = latest.Optimism.ToString("F2"),
            ["created"] = AgentCoreProcessor.Util.TimeUtil.ToLocalSafe(latest.CreatedAt).ToString("yyyy-MM-dd HH:mm:ss")
        };
        return new DataResult { Data = data };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

/// <summary>
/// 最近 N 条情绪快照列表（五轴值 + CreatedAt），走 EmotionSnapshots.GetRecentAsync。
/// </summary>
internal class EmotionSource : IDataSource
{
    private readonly MasterEngine _engine;
    /// <summary>一次拉取上限。</summary>
    private const int FetchLimit = 100;

    public EmotionSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var recent = await _engine.EmotionSnapshots.GetRecentAsync(FetchLimit);

        var arr = new JsonArray();
        foreach (var s in recent)
        {
            arr.Add(new JsonObject
            {
                ["id"] = s.Id,
                ["excitement"] = s.Excitement.ToString("F2"),
                ["joy"] = s.Joy.ToString("F2"),
                ["alertness"] = s.Alertness.ToString("F2"),
                ["satisfaction"] = s.Satisfaction.ToString("F2"),
                ["optimism"] = s.Optimism.ToString("F2"),
                ["created"] = AgentCoreProcessor.Util.TimeUtil.ToLocalSafe(s.CreatedAt).ToString("MM-dd HH:mm:ss")
            });
        }
        return new DataResult { Data = arr, TotalCount = recent.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}


// ================ 通用辅助 ================

/// <summary>从查询过滤条件中取指定字段的值（空值返回 null）。</summary>
internal static class MemoryQueryHelper
{
    public static string? GetFilterValue(DataQuery? query, string field)
    {
        if (query?.Filters == null) return null;
        foreach (var f in query.Filters)
        {
            if (f.Field == field && !string.IsNullOrEmpty(f.Value))
                return f.Value;
        }
        return null;
    }
}
