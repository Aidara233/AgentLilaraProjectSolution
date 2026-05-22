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
                                new() { Field = "systemState", Label = "系统循环", Type = StatusFieldType.Indicator },
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

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var summary = _engine.GetActiveEngineSummary();
        var total = summary.Sum(s => s.Count);
        var breakdown = summary.Count > 0
            ? string.Join("  ", summary.Select(s => $"{s.Type}×{s.Count}"))
            : "无";

        var systemCheck = _engine.GetSpawnCheck<SystemEngineSpawnCheck>();
        var systemSnap = systemCheck?.GetSystemSnapshot();
        var sleepState = _engine.CurrentSleepState;

        var data = new JsonObject
        {
            ["total"] = total.ToString(),
            ["breakdown"] = breakdown,
            ["systemState"] = systemSnap?.IsAlive == true ? "运行中" : "停止",
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

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var arr = new JsonArray();

        // Channel 引擎（多实例）
        var workerCheck = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
        if (workerCheck != null)
        {
            foreach (var (_, w) in workerCheck.GetActiveChannels())
            {
                if (!w.IsAlive) continue;
                var snap = w.GetSnapshot();
                arr.Add(new JsonObject
                {
                    ["type"] = "Channel",
                    ["name"] = snap.ChannelName ?? $"#{snap.ChannelId}",
                    ["status"] = snap.IsBusy ? "处理中" : "等待",
                    ["detail"] = $"冲动 {snap.Impulse:F1} | 轮次 {snap.TotalRounds} | {(snap.IsWorkingMode ? "Working" : "Express")}",
                    ["_link"] = $"/p/engines/channel_{snap.ChannelId}"
                });
            }
        }

        // System 引擎
        var systemCheck = _engine.GetSpawnCheck<SystemEngineSpawnCheck>();
        var systemSnap = systemCheck?.GetSystemSnapshot();
        if (systemSnap?.IsAlive == true)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "System",
                ["name"] = "系统循环",
                ["status"] = "运行中",
                ["detail"] = $"任务队列 {systemSnap.TaskQueueDepth} | 子agent {systemSnap.ActiveSubAgentCount}",
                ["_link"] = "/p/engines/system"
            });
        }

        // Dream 引擎
        var dreamCheck = _engine.GetSpawnCheck<DreamEngineSpawnCheck>();
        var hasDream = _engine.HasActiveEngine("Dream");
        if (hasDream && dreamCheck != null)
        {
            var dreamSnap = dreamCheck.GetDreamSnapshot(true);
            var progress = dreamSnap.FragmentsTotal > 0
                ? $"{dreamSnap.FragmentsCompleted}/{dreamSnap.FragmentsTotal}"
                : "准备中";
            var fragment = dreamSnap.CurrentFragment ?? "—";
            arr.Add(new JsonObject
            {
                ["type"] = "Dream",
                ["name"] = "做梦",
                ["status"] = "进行中",
                ["detail"] = $"片段 {progress} | 当前: {fragment}",
                ["_link"] = "/p/engines/dream"
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

        // Review 引擎（通过 GetActiveEnginesSnapshot 找）
        var allEngines = _engine.GetActiveEnginesSnapshot();
        foreach (var eng in allEngines.Where(e => e.EngineType == "Review"))
        {
            var detail = "运行中";
            if (eng is ReviewEngine review)
            {
                detail = $"token {review.TokensUsed / 1000}k | 游标 ch{review.CursorChannelId}";
            }
            arr.Add(new JsonObject
            {
                ["type"] = "Review",
                ["name"] = "复盘",
                ["status"] = "运行中",
                ["detail"] = detail,
                ["_link"] = "/p/engines/review"
            });
        }

        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}
