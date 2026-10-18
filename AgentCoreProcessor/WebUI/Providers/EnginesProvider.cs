using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Vision;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class EnginesProvider : IWebUIProvider
{
    public string Id => "core-engines";
    public string DisplayName => "引擎";

    private readonly MasterEngine _engine;

    public EnginesProvider(MasterEngine engine)
    {
        _engine = engine;
        Pages = new List<PageDefinition>
        {
            new()
            {
                Route = "engines",
                Meta = new PageMeta { Title = "引擎列表", Icon = "bi-cpu", Group = "", Order = -90 },
                Cards = new List<CardDefinition>
                {
                    new()
                    {
                        Id = "engine-summary",
                        Type = CardType.Status,
                        DataSourceId = "engines-summary",
                        Title = "引擎总览",
                        Schema = new StatusSchema
                        {
                            Fields = new()
                            {
                                new() { Field = "total", Label = "活跃总数" },
                                new() { Field = "breakdown", Label = "类型分布" },
                                new() { Field = "sessionState", Label = "会话引擎", Type = StatusFieldType.Indicator },
                                new() { Field = "sleepState", Label = "睡眠状态", Type = StatusFieldType.Badge }
                            }
                        },
                        Layout = new CardLayout { PreferredCols = 12 }
                    },
                    new()
                    {
                        Id = "engine-list",
                        Type = CardType.Table,
                        DataSourceId = "engines-list",
                        Title = "活跃引擎",
                        Schema = new TableSchema
                        {
                            Columns = new()
                            {
                                new() { Field = "type", Header = "类型", Width = "100px", Format = ColumnFormat.Badge },
                                new() { Field = "name", Header = "标识", Format = ColumnFormat.Link },
                                new() { Field = "status", Header = "状态", Width = "80px", Format = ColumnFormat.Badge },
                                new() { Field = "detail", Header = "详情" }
                            }
                        },
                        Layout = new CardLayout { PreferredCols = 12 }
                    }
                },
                DataSources = new List<DataSourceDefinition>
                {
                    new() { Id = "engines-summary", Source = new EngineSummarySource(engine) },
                    new() { Id = "engines-list", Source = new EngineListSource(engine) }
                }
            }
        };
    }

    public IReadOnlyList<PageDefinition> Pages { get; }
}

// ---- 数据源 ----

internal class EngineSummarySource : IDataSource
{
    private readonly MasterEngine _engine;
    public EngineSummarySource(MasterEngine engine) => _engine = engine;

    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var summary = _engine.GetActiveEngineSummary();
        var total = summary.Sum(s => s.Count);
        var breakdown = summary.Count > 0
            ? string.Join("  ", summary.Select(s => $"{s.Type}×{s.Count}"))
            : "无";

        var sessionEngine = _engine.GetActiveEnginesSnapshot()
            .OfType<AgentCoreProcessor.Engine.Session.SessionEngine>().FirstOrDefault();
        var sleepState = _engine.CurrentSleepState;

        var data = new JsonObject
        {
            ["total"] = total.ToString(),
            ["breakdown"] = breakdown,
            ["sessionState"] = sessionEngine?.IsAlive == true ? "运行中" : "停止",
            ["sleepState"] = sleepState != SleepState.None ? sleepState.ToString() : "清醒"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class EngineListSource : IDataSource
{
    private readonly MasterEngine _engine;
    public EngineListSource(MasterEngine engine) => _engine = engine;

    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var arr = new JsonArray();

        // Channel 会话（express/working 承载频道响应，替代原 ExpressEngine）
        var sessionEngine = _engine.GetActiveEnginesSnapshot()
            .OfType<AgentCoreProcessor.Engine.Session.SessionEngine>().FirstOrDefault();
        if (sessionEngine != null)
        {
            foreach (var snap in sessionEngine.GetChannelSessionSnapshots())
            {
                if (!snap.IsAlive) continue;
                arr.Add(new JsonObject
                {
                    ["type"] = snap.Type == "express" ? "Express" : "Working",
                    ["name"] = $"#{snap.ChannelId}",
                    ["status"] = "活跃",
                    ["detail"] = $"类型 {snap.Type} | 轮次 {snap.RoundCount} | 会话",
                    ["_link"] = $"/p/sessions/{snap.SessionId}"
                });
            }
        }

        // Session 引擎
        if (sessionEngine?.IsAlive == true)
        {
            var sessions = sessionEngine.GetActiveSessions();
            var todoCount = sessionEngine.GetPendingTodos().Count;
            arr.Add(new JsonObject
            {
                ["type"] = "Session",
                ["name"] = "会话引擎",
                ["status"] = "运行中",
                ["detail"] = $"活跃会话 {sessions.Count} | 待办 {todoCount}",
                ["_link"] = "/p/sessions"
            });
        }

        // 其他基础设施引擎：会话运行在 SessionEngine 内，未单独启动时也应当可见。
        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Session", "Vision", "Express" };
        foreach (var summary in _engine.GetActiveEngineSummary())
        {
            if (linked.Contains(summary.Type)) continue;
            arr.Add(new JsonObject
            {
                ["type"] = summary.Type,
                ["name"] = summary.Type,
                ["status"] = "运行中",
                ["detail"] = $"实例 ×{summary.Count}"
            });
        }

        // Vision 引擎
        var visionCheck = _engine.GetSpawnCheck<VisionEngineSpawnCheck>();
        var visionInstance = visionCheck?.ActiveInstance;
        if (visionInstance != null)
        {
            var vSnap = visionInstance.GetSnapshot();
            arr.Add(new JsonObject
            {
                ["type"] = "Vision",
                ["name"] = "视觉",
                ["status"] = vSnap.IsBusy ? "处理中" : "空闲",
                ["detail"] = $"已处理 {vSnap.TotalProcessed} | 错误 {vSnap.VisionErrors + vSnap.OcrErrors}",
                ["_link"] = "/p/engines/vision"
            });
        }

        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}
