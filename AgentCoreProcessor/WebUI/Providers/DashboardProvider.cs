using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class DashboardProvider : IWebUIProvider
{
    public string Id => "core-dashboard";
    public string DisplayName => "总览";

    private readonly MasterEngine _engine;

    public DashboardProvider(MasterEngine engine)
    {
        _engine = engine;
        Pages = new List<PageDefinition>
        {
            new()
            {
                Route = "dashboard",
                Meta = new PageMeta { Title = "总览", Icon = "bi-speedometer2", Group = "", Order = -100 },
                Cards = new List<CardDefinition>
                {
                    new()
                    {
                        Id = "sys-status",
                        Type = CardType.Status,
                        DataSourceId = "dashboard-status",
                        Title = "系统状态",
                        Schema = new StatusSchema
                        {
                            Fields = new()
                            {
                                new() { Field = "state", Label = "状态", Type = StatusFieldType.Indicator },
                                new() { Field = "idleDuration", Label = "空闲时长" },
                                new() { Field = "lastMessage", Label = "上次消息" },
                                new() { Field = "muteMode", Label = "静音模式", Type = StatusFieldType.Badge },
                                new() { Field = "sleepState", Label = "睡眠", Type = StatusFieldType.Badge }
                            }
                        },
                        Layout = new CardLayout { PreferredCols = 6 }
                    },
                    new()
                    {
                        Id = "engine-summary",
                        Type = CardType.Status,
                        DataSourceId = "dashboard-engines",
                        Title = "引擎概览",
                        Schema = new StatusSchema
                        {
                            Fields = new()
                            {
                                new() { Field = "activeEngines", Label = "活跃引擎" },
                                new() { Field = "dreamState", Label = "做梦", Type = StatusFieldType.Badge },
                                new() { Field = "systemEngine", Label = "系统循环", Type = StatusFieldType.Indicator },
                                new() { Field = "taskQueue", Label = "任务队列" },
                                new() { Field = "subAgents", Label = "子agent" }
                            }
                        },
                        Layout = new CardLayout { PreferredCols = 6 }
                    },
                    new()
                    {
                        Id = "active-channels",
                        Type = CardType.Table,
                        DataSourceId = "dashboard-channels",
                        Title = "活跃频道",
                        Schema = new TableSchema
                        {
                            Columns = new()
                            {
                                new() { Field = "channelId", Header = "频道", Width = "80px" },
                                new() { Field = "name", Header = "名称" },
                                new() { Field = "mode", Header = "模式", Format = ColumnFormat.Badge },
                                new() { Field = "status", Header = "状态", Format = ColumnFormat.Badge },
                                new() { Field = "impulse", Header = "冲动值", Width = "80px" },
                                new() { Field = "rounds", Header = "轮次", Width = "60px" }
                            }
                        },
                        Layout = new CardLayout { PreferredCols = 12 }
                    }
                },
                DataSources = new List<DataSourceDefinition>
                {
                    new() { Id = "dashboard-status", Source = new DashboardStatusSource(engine) },
                    new() { Id = "dashboard-engines", Source = new DashboardEngineSource(engine) },
                    new() { Id = "dashboard-channels", Source = new DashboardChannelSource(engine) }
                }
            }
        };
    }

    public IReadOnlyList<PageDefinition> Pages { get; }
}

// ---- 数据源 ----

internal class DashboardStatusSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DashboardStatusSource(MasterEngine engine) => _engine = engine;

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var sleepState = _engine.CurrentSleepState;

        var data = new JsonObject
        {
            ["state"] = _engine.IsIdle ? "空闲" : "忙碌",
            ["idleDuration"] = FormatDuration(_engine.IdleDuration),
            ["lastMessage"] = _engine.LastMessageTime.ToString("HH:mm:ss"),
            ["muteMode"] = _engine.MuteMode ? "开启" : "关闭",
            ["sleepState"] = sleepState != SleepState.None ? sleepState.ToString() : "清醒"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}

internal class DashboardEngineSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DashboardEngineSource(MasterEngine engine) => _engine = engine;

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var summary = _engine.GetActiveEngineSummary();
        var engineStr = summary.Count > 0
            ? string.Join(", ", summary.Select(s => $"{s.Type}×{s.Count}"))
            : "无";

        var systemCheck = _engine.GetSpawnCheck<SystemEngineSpawnCheck>();
        var hasDream = _engine.HasActiveEngine("Dream");

        var dreamLabel = hasDream ? "进行中" : (_engine.CurrentSleepState != SleepState.None ? "睡眠中" : "清醒");

        var systemSnap = systemCheck?.GetSystemSnapshot();

        var data = new JsonObject
        {
            ["activeEngines"] = engineStr,
            ["dreamState"] = dreamLabel,
            ["systemEngine"] = systemSnap?.IsAlive == true ? "运行中" : "停止",
            ["taskQueue"] = (systemSnap?.TaskQueueDepth ?? 0).ToString(),
            ["subAgents"] = (systemSnap?.ActiveSubAgentCount ?? 0).ToString()
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class DashboardChannelSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DashboardChannelSource(MasterEngine engine) => _engine = engine;

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var workerCheck = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
        var arr = new JsonArray();

        if (workerCheck != null)
        {
            foreach (var (_, w) in workerCheck.GetActiveChannels())
            {
                if (!w.IsAlive) continue;
                var snap = w.GetSnapshot();
                arr.Add(new JsonObject
                {
                    ["channelId"] = snap.ChannelId,
                    ["name"] = snap.ChannelName ?? $"#{snap.ChannelId}",
                    ["mode"] = snap.IsWorkingMode ? "Working" : "Express",
                    ["status"] = snap.IsBusy ? "处理中" : "等待",
                    ["impulse"] = snap.Impulse.ToString("F1"),
                    ["rounds"] = snap.TotalRounds
                });
            }
        }

        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}
