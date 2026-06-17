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
        Meta = new PageMeta { Title = "Token 统计", Icon = "bi-bar-chart", Group = "调试", Order = 110 },
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
        Meta = new PageMeta { Title = "模型调用日志", Icon = "bi-file-earmark-code", Group = "调试", Order = 112 },
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
                        new() { Field = "time", Header = "时间", Width = "140px" },
                        new() { Field = "coreName", Header = "Core", Width = "80px" },
                        new() { Field = "model", Header = "模型", Width = "120px" },
                        new() { Field = "tokens", Header = "Token", Width = "100px" },
                        new() { Field = "cache", Header = "缓存", Width = "80px" },
                        new() { Field = "status", Header = "状态", Width = "60px" },
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
                        new() { Field = "systemPrompt", Label = "系统提示词", IsMultiline = true },
                        new() { Field = "messages", Label = "消息历史", IsMultiline = true },
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
        var logs = await _engine.ModelCallLogs.GetRecentAsync(2000, includeErrors: false);
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

        // 注意：TableCard 使用客户端分页，返回所有数据（最多2000条）
        var arr = new JsonArray();
        foreach (var log in list)
        {
            var tokens = log.IsError ? "—" : $"{Fmt(log.InputTokens)}→{Fmt(log.OutputTokens)}";
            var cache = log.CacheReadTokens + log.CacheHitTokens + log.CacheCreationTokens;
            var cacheStr = log.IsError ? "—" : (cache > 0 ? Fmt(cache) : "—");

            arr.Add(new JsonObject
            {
                ["id"] = log.Id,
                ["time"] = log.Timestamp.ToString("MM-dd HH:mm:ss"),
                ["coreName"] = log.CoreName,
                ["model"] = log.Model ?? "—",
                ["provider"] = log.Provider ?? "—",
                ["tokens"] = tokens,
                ["cache"] = cacheStr,
                ["status"] = log.IsError ? "❌ 失败" : "✓",
                ["logFileName"] = log.LogFileName ?? "",
            });
        }
        return new DataResult { Data = arr, TotalCount = arr.Count };
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
            var isError = root["error"]?.Value<bool>() ?? false;

            // Token 用量（含 cache miss）
            var usage = root["usage"];
            var usageStr = "—";
            if (usage != null)
            {
                var parts = new List<string> { $"{usage["inputTokens"]} in / {usage["outputTokens"]} out" };
                var cacheParts = new List<string>();
                if (usage["cacheReadTokens"]?.Value<long>() > 0)
                    cacheParts.Add($"read:{usage["cacheReadTokens"]}");
                if (usage["cacheCreationTokens"]?.Value<long>() > 0)
                    cacheParts.Add($"create:{usage["cacheCreationTokens"]}");
                if (usage["cacheHitTokens"]?.Value<long>() > 0)
                    cacheParts.Add($"hit:{usage["cacheHitTokens"]}");
                if (usage["cacheMissTokens"]?.Value<long>() > 0)
                    cacheParts.Add($"miss:{usage["cacheMissTokens"]}");
                if (cacheParts.Count > 0)
                    parts.Add($"cache({string.Join(" ", cacheParts)})");
                usageStr = string.Join(" ", parts);
            }

            // 工具列表
            var toolsArr = root["tools"] as JArray;
            var toolsStr = toolsArr != null && toolsArr.Count > 0
                ? string.Join(", ", toolsArr.Select(t => t.ToString()))
                : "—";

            var systemPrompt = root["systemPrompt"]?.ToString();
            var systemPromptDisplay = systemPrompt
                ?? (root["systemPromptHash"]?.ToString() is string hash ? $"[hash: {hash}]" : "—");

            // 消息摘要（含 contentParts）
            var messagesDisplay = "—";
            if (root["messages"] is JArray msgArr && msgArr.Count > 0)
            {
                var msgLines = new List<string>();
                foreach (var m in msgArr)
                {
                    var role = m["role"]?.ToString() ?? "?";
                    var roleLabel = role switch
                    {
                        "user" => "用户",
                        "assistant" => "助手",
                        "system" => "系统",
                        "tool" => "工具",
                        _ => role
                    };
                    if (m["contentParts"] is JArray parts && parts.Count > 0)
                    {
                        var partDescs = new List<string>();
                        foreach (var p in parts)
                        {
                            var type = p["type"]?.ToString() ?? "?";
                            switch (type)
                            {
                                case "text" when p["text"]?.ToString() is string t:
                                    partDescs.Add(t);
                                    break;
                                case "image" when p["image"] != null:
                                    var img = p["image"];
                                    var detail = img?["path"]?.ToString()
                                        ?? (img?["base64Length"] != null ? $"base64({img["base64Length"]}B)" : "—");
                                    partDescs.Add($"[图片: {detail}]");
                                    break;
                                case "tool_use":
                                    partDescs.Add($"[工具调用: {p["toolName"]}]");
                                    break;
                                case "tool_result":
                                    partDescs.Add(p["isError"]?.Value<bool>() == true
                                        ? "[工具结果: 错误]"
                                        : "[工具结果]");
                                    break;
                                default:
                                    partDescs.Add($"[{type}]");
                                    break;
                            }
                        }
                        msgLines.Add($"═══ {roleLabel} ═══\n{string.Join(" | ", partDescs)}");
                    }
                    else
                    {
                        var content = m["content"]?.ToString() ?? "";
                        msgLines.Add($"═══ {roleLabel} ═══\n{content}");
                    }
                }
                messagesDisplay = string.Join("\n", msgLines);
            }

            var thinking = root["thinking"]?.ToString();
            var output = root["output"]?.ToString();

            var modelInfo = isError
                ? $"⚠️ {model} / {provider} (调用失败)"
                : $"{model} / {provider}";

            return Task.FromResult(new DataResult
            {
                Data = new JsonObject
                {
                    ["caller"] = caller,
                    ["modelInfo"] = modelInfo,
                    ["usage"] = usageStr,
                    ["tools"] = toolsStr,
                    ["systemPrompt"] = systemPromptDisplay,
                    ["messages"] = messagesDisplay,
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
