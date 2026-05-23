using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.WebUI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class LogsProvider : IWebUIProvider
{
    public string Id => "logs";
    public string DisplayName => "日志";

    private readonly MasterEngine _engine;

    public LogsProvider(MasterEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<PageDefinition> Pages => new List<PageDefinition>
    {
        BuildTokensPage(),
        BuildModelPage(),
    };

    // ================ Token 统计 ================

    private PageDefinition BuildTokensPage() => new()
    {
        Route = "logs/tokens",
        Meta = new PageMeta { Title = "Token 统计", Icon = "bi-bar-chart", Group = "日志", Order = 20 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "tokens-cache", Type = CardType.Status, DataSourceId = "tokens-cache", Title = "缓存概览",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "totalInput", Label = "总输入 Token" },
                        new() { Field = "cacheHit", Label = "缓存命中" },
                        new() { Field = "cacheCreate", Label = "缓存创建" },
                        new() { Field = "hitRate", Label = "命中率", Type = StatusFieldType.Badge },
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "tokens-by-core", Type = CardType.Table, DataSourceId = "tokens-by-core", Title = "按 Core",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "coreName", Header = "Core" },
                        new() { Field = "callCount", Header = "调用", Width = "70px" },
                        new() { Field = "totalInput", Header = "输入", Width = "80px" },
                        new() { Field = "totalOutput", Header = "输出", Width = "80px" },
                        new() { Field = "cacheHit", Header = "缓存命中", Width = "90px" },
                    },
                    Searchable = false, Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "tokens-by-model", Type = CardType.Table, DataSourceId = "tokens-by-model", Title = "按模型",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "model", Header = "模型" },
                        new() { Field = "provider", Header = "提供商", Width = "80px" },
                        new() { Field = "callCount", Header = "调用", Width = "70px" },
                        new() { Field = "totalInput", Header = "输入", Width = "80px" },
                        new() { Field = "totalOutput", Header = "输出", Width = "80px" },
                        new() { Field = "cacheHit", Header = "缓存命中", Width = "90px" },
                    },
                    Searchable = false, Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 6 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "tokens-cache", Source = new TokenCacheSource(_engine) },
            new() { Id = "tokens-by-core", Source = new TokenByCoreSource(_engine) },
            new() { Id = "tokens-by-model", Source = new TokenByModelSource(_engine) },
        }
    };

    // ================ 模型调用日志 ================

    private PageDefinition BuildModelPage() => new()
    {
        Route = "logs/model",
        Meta = new PageMeta { Title = "模型调用日志", Icon = "bi-file-earmark-code", Group = "日志", Order = 30 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "model-list", Type = CardType.Table, DataSourceId = "model-list", Title = "调用历史",
                LinkEvent = "model-call-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "time", Header = "时间", Width = "130px" },
                        new() { Field = "coreName", Header = "Core", Width = "80px" },
                        new() { Field = "model", Header = "模型", Width = "120px" },
                        new() { Field = "tokens", Header = "Token", Width = "100px" },
                        new() { Field = "cache", Header = "缓存", Width = "80px" },
                    },
                    Searchable = true, Paginated = true, DefaultPageSize = 30
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "model-detail", Type = CardType.Status, DataSourceId = "model-detail", Title = "调用详情",
                ListenEvent = "model-call-selected",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "caller", Label = "调用来源" },
                        new() { Field = "modelInfo", Label = "模型/提供商" },
                        new() { Field = "usage", Label = "Token 用量" },
                        new() { Field = "tools", Label = "工具列表", IsMultiline = true },
                        new() { Field = "thinking", Label = "思考过程", IsMultiline = true },
                        new() { Field = "output", Label = "输出", IsMultiline = true },
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "model-list", Source = new ModelListSource(_engine) },
            new() { Id = "model-detail", Source = new ModelDetailSource(_engine) },
        }
    };
}

// ================ Token 数据源 ================

internal class TokenCacheSource : IDataSource
{
    private readonly MasterEngine _engine;
    public TokenCacheSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var logs = await _engine.ModelCallLogs.GetRecentAsync(2000);
        long totalInput = 0, totalCacheRead = 0, totalCacheCreation = 0, totalCacheHit = 0;
        foreach (var log in logs)
        {
            totalInput += log.InputTokens;
            totalCacheRead += log.CacheReadTokens;
            totalCacheCreation += log.CacheCreationTokens;
            totalCacheHit += log.CacheHitTokens;
        }

        var totalCacheable = totalInput + totalCacheRead + totalCacheCreation;
        var hitRate = totalCacheable > 0
            ? (double)(totalCacheRead + totalCacheHit) / totalCacheable * 100 : 0;

        return new DataResult
        {
            Data = new JsonObject
            {
                ["totalInput"] = FormatTokens(totalInput),
                ["cacheHit"] = FormatTokens(totalCacheRead + totalCacheHit),
                ["cacheCreate"] = FormatTokens(totalCacheCreation),
                ["hitRate"] = $"{hitRate:F1}%"
            }
        };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });

    private static string FormatTokens(long t)
    {
        if (t < 1000) return t.ToString();
        if (t < 1_000_000) return $"{t / 1000.0:F1}k";
        return $"{t / 1_000_000.0:F2}M";
    }
}

internal class TokenByCoreSource : IDataSource
{
    private readonly MasterEngine _engine;
    public TokenByCoreSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var summaries = await _engine.ModelCallLogs.GetByCoreAsync();
        var arr = new JsonArray();
        foreach (var s in summaries)
        {
            arr.Add(new JsonObject
            {
                ["coreName"] = s.CoreName,
                ["callCount"] = s.CallCount,
                ["totalInput"] = Fmt(s.TotalInput),
                ["totalOutput"] = Fmt(s.TotalOutput),
                ["cacheHit"] = Fmt(s.TotalCacheRead + s.TotalCacheHit)
            });
        }
        return new DataResult { Data = arr, TotalCount = arr.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });

    private static string Fmt(long t) => t < 1000 ? t.ToString() : t < 1_000_000 ? $"{t / 1000.0:F1}k" : $"{t / 1_000_000.0:F2}M";
}

internal class TokenByModelSource : IDataSource
{
    private readonly MasterEngine _engine;
    public TokenByModelSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var summaries = await _engine.ModelCallLogs.GetByModelAsync();
        var arr = new JsonArray();
        foreach (var s in summaries)
        {
            arr.Add(new JsonObject
            {
                ["model"] = s.Model ?? "unknown",
                ["provider"] = s.Provider ?? "—",
                ["callCount"] = s.CallCount,
                ["totalInput"] = Fmt(s.TotalInput),
                ["totalOutput"] = Fmt(s.TotalOutput),
                ["cacheHit"] = Fmt(s.TotalCacheRead + s.TotalCacheHit)
            });
        }
        return new DataResult { Data = arr, TotalCount = arr.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });

    private static string Fmt(long t) => t < 1000 ? t.ToString() : t < 1_000_000 ? $"{t / 1000.0:F1}k" : $"{t / 1_000_000.0:F2}M";
}

// ================ 模型调用日志数据源 ================

internal class ModelListSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ModelListSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 30;
        var all = await _engine.ModelCallLogs.GetRecentAsync(2000);

        var filtered = all.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var kw = query.Search.Trim().ToLowerInvariant();
            filtered = filtered.Where(l =>
                (l.CoreName?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (l.Model?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (l.Provider?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = filtered.ToList();
        var total = list.Count;
        var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var arr = new JsonArray();
        foreach (var log in paged)
        {
            var tokens = $"{Fmt(log.InputTokens)}→{Fmt(log.OutputTokens)}";
            var cache = log.CacheReadTokens + log.CacheHitTokens;
            var cacheStr = cache > 0 ? Fmt(cache) : "—";

            arr.Add(new JsonObject
            {
                ["id"] = log.Id,
                ["time"] = log.Timestamp.ToString("MM-dd HH:mm:ss"),
                ["coreName"] = log.CoreName,
                ["model"] = log.Model ?? "—",
                ["provider"] = log.Provider ?? "—",
                ["tokens"] = tokens,
                ["cache"] = cacheStr,
                ["logFileName"] = log.LogFileName ?? "",
            });
        }
        return new DataResult { Data = arr, TotalCount = total };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });

    private static string Fmt(long t) => t < 1000 ? t.ToString() : t < 1_000_000 ? $"{t / 1000.0:F1}k" : $"{t / 1_000_000.0:F2}M";
}

internal class ModelDetailSource : IDataSource
{
    private readonly MasterEngine _engine;
    private string _selectedFile = "";
    public ModelDetailSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        if (query?.Extra is JsonObject extra)
        {
            var f = extra["logFileName"]?.ToString() ?? "";
            if (f != _selectedFile)
            {
                _selectedFile = f;
            }
        }

        if (string.IsNullOrEmpty(_selectedFile))
            return Task.FromResult(new DataResult { Data = new JsonObject() });

        var logPath = Path.Combine(PathConfig.LogPath, "Model", _selectedFile);
        if (!File.Exists(logPath))
            return Task.FromResult(new DataResult
            {
                Data = new JsonObject { ["caller"] = "日志文件不存在", ["modelInfo"] = logPath }
            });

        try
        {
            var json = File.ReadAllText(logPath);
            var root = JObject.Parse(json);

            var caller = root["caller"]?.ToString() ?? "—";
            var model = root["model"]?.ToString() ?? "—";
            var provider = root["provider"]?.ToString() ?? "—";
            var usage = root["usage"];
            var usageStr = usage != null
                ? $"{usage["inputTokens"]} in / {usage["outputTokens"]} out" +
                  (usage["cacheReadTokens"]?.Value<long>() > 0 || usage["cacheHitTokens"]?.Value<long>() > 0
                      ? $" (cache read:{usage["cacheReadTokens"]}+hit:{usage["cacheHitTokens"]})" : "")
                : "—";

            var toolsArr = root["tools"] as JArray;
            var toolsStr = toolsArr != null && toolsArr.Count > 0
                ? string.Join(", ", toolsArr.Select(t => t.ToString()))
                : "—";

            var thinking = root["thinking"]?.ToString();
            var output = root["output"]?.ToString();

            return Task.FromResult(new DataResult
            {
                Data = new JsonObject
                {
                    ["caller"] = caller,
                    ["modelInfo"] = $"{model} / {provider}",
                    ["usage"] = usageStr,
                    ["tools"] = toolsStr,
                    ["thinking"] = thinking ?? "—",
                    ["output"] = output ?? "—",
                }
            });
        }
        catch
        {
            return Task.FromResult(new DataResult
            {
                Data = new JsonObject { ["caller"] = "解析失败", ["modelInfo"] = _selectedFile }
            });
        }
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false });
}
