using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

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
        BuildStatusPage(),
        BuildHistoryPage(),
        BuildSessionDetailPage(),
        BuildSleepPage()
    };

    // ---- 状态页 ----

    private PageDefinition BuildStatusPage() => new()
    {
        Route = "dream",
        Meta = new PageMeta { Title = "状态", Icon = "bi-moon-stars", Group = "做梦引擎", Order = 50 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "dream-status", Type = CardType.Status, DataSourceId = "dream-status", Title = "做梦引擎",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "state", Label = "状态", Type = StatusFieldType.Indicator },
                        new() { Field = "level", Label = "睡眠等级", Type = StatusFieldType.Badge },
                        new() { Field = "progress", Label = "进度", Type = StatusFieldType.Progress },
                        new() { Field = "current_fragment", Label = "当前片段" },
                        new() { Field = "fragment_elapsed", Label = "片段耗时" },
                        new() { Field = "input_desc", Label = "输入描述" },
                        new() { Field = "force_flag", Label = "强制触发", Type = StatusFieldType.Badge }
                    },
                    Actions = new()
                    {
                        new() { Id = "force-deep", Label = "强制大睡", Icon = "bi-moon" },
                        new() { Id = "force-nap", Label = "强制小睡", Icon = "bi-cloud" },
                        new() { Id = "force-daydream", Label = "强制走神", Icon = "bi-cloud-haze" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "dream-stats", Type = CardType.Status, DataSourceId = "dream-stats", Title = "统计",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "total_sessions", Label = "总会话数" },
                        new() { Field = "last_daydream", Label = "上次走神" },
                        new() { Field = "baseline", Label = "7天基线" },
                        new() { Field = "red_days", Label = "连续红天" },
                        new() { Field = "today_temp_peak", Label = "今日临时峰值" },
                        new() { Field = "today_processed", Label = "今日已处理" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "dream-fragments", Type = CardType.Table, DataSourceId = "dream-fragments", Title = "当前/最近会话片段",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "seq", Header = "#", Width = "40px" },
                        new() { Field = "type", Header = "类型", Width = "100px", Format = ColumnFormat.Badge },
                        new() { Field = "status", Header = "状态", Width = "70px", Format = ColumnFormat.Badge },
                        new() { Field = "duration", Header = "耗时", Width = "70px" },
                        new() { Field = "summary", Header = "摘要" }
                    },
                    DefaultPageSize = 50
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "dream-status", Source = new DreamStatusSource(_engine) },
            new() { Id = "dream-stats", Source = new DreamStatsSource(_engine) },
            new() { Id = "dream-fragments", Source = new DreamActiveFragmentsSource(_engine) }
        }
    };

    // ---- 历史页 ----

    private PageDefinition BuildHistoryPage() => new()
    {
        Route = "dream/history",
        Meta = new PageMeta { Title = "历史", Icon = "bi-clock-history", Group = "做梦引擎", Order = 51 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "dream-sessions", Type = CardType.Table, DataSourceId = "dream-sessions", Title = "做梦会话",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "time", Header = "时间", Width = "140px" },
                        new() { Field = "level", Header = "等级", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "fragments", Header = "片段数", Width = "70px" },
                        new() { Field = "duration", Header = "耗时", Width = "80px" },
                        new() { Field = "interrupted", Header = "打断", Width = "60px", Format = ColumnFormat.Badge }
                    },
                    DefaultPageSize = 20
                },
                Layout = new CardLayout { PreferredCols = 12 }
            },
            new()
            {
                Id = "dream-daily-table", Type = CardType.Table, DataSourceId = "dream-daily-table", Title = "每日统计（近7天）",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "date", Header = "日期", Width = "100px" },
                        new() { Field = "temp_peak", Header = "临时峰值", Width = "80px" },
                        new() { Field = "processed", Header = "已处理", Width = "70px" },
                        new() { Field = "undreamed_peak", Header = "未做梦峰值", Width = "90px" }
                    },
                    Paginated = false
                },
                Layout = new CardLayout { PreferredCols = 8 }
            },
            new()
            {
                Id = "dream-stats-cards", Type = CardType.Status, DataSourceId = "dream-stats-cards", Title = "统计概览",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "baseline_avg", Label = "基线均值" },
                        new() { Field = "baseline_update", Label = "基线更新" },
                        new() { Field = "red_days", Label = "连续红天", Type = StatusFieldType.Badge }
                    }
                },
                Layout = new CardLayout { PreferredCols = 4 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "dream-sessions", Source = new DreamSessionsSource(_engine) },
            new() { Id = "dream-daily-table", Source = new DreamDailyStatsSource(_engine) },
            new() { Id = "dream-stats-cards", Source = new DreamStatsCardsSource(_engine) }
        }
    };

    // ---- 会话详情页（隐藏导航） ----

    private PageDefinition BuildSessionDetailPage() => new()
    {
        Route = "dream/history/{id}",
        Meta = new PageMeta { Title = "会话详情", Icon = "bi-list-nested", Group = "做梦引擎", ShowInNav = false },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "session-info", Type = CardType.Status, DataSourceId = "session-info", Title = "会话信息",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "level", Label = "等级", Type = StatusFieldType.Badge },
                        new() { Field = "time_range", Label = "时间" },
                        new() { Field = "fragments", Label = "片段数" },
                        new() { Field = "interrupted", Label = "被打断", Type = StatusFieldType.Badge }
                    }
                },
                Layout = new CardLayout { PreferredCols = 7, Order = 0 }
            },
            new()
            {
                Id = "fragment-detail", Type = CardType.Status, DataSourceId = "fragment-detail", Title = "片段详情",
                ListenEvent = "fragment-selected",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "type", Label = "类型", Type = StatusFieldType.Badge },
                        new() { Field = "time", Label = "时间" },
                        new() { Field = "duration", Label = "耗时" },
                        new() { Field = "success", Label = "结果", Type = StatusFieldType.Badge },
                        new() { Field = "summary", Label = "摘要" },
                        new() { Field = "input_memories", Label = "输入素材", IsMultiline = true },
                        new() { Field = "changes", Label = "变更明细", IsMultiline = true }
                    }
                },
                Layout = new CardLayout { PreferredCols = 5, RowSpan = 2, Order = 1 }
            },
            new()
            {
                Id = "session-fragments", Type = CardType.Table, DataSourceId = "session-fragments", Title = "片段列表",
                LinkEvent = "fragment-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "seq", Header = "#", Width = "40px" },
                        new() { Field = "type", Header = "类型", Width = "100px", Format = ColumnFormat.Badge },
                        new() { Field = "success", Header = "结果", Width = "60px", Format = ColumnFormat.Badge },
                        new() { Field = "duration", Header = "耗时", Width = "70px" },
                        new() { Field = "summary", Header = "摘要" }
                    },
                    DefaultPageSize = 50
                },
                Layout = new CardLayout { PreferredCols = 7, Height = "calc(100vh - 480px)", Order = 2 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "session-info", Source = new DreamSessionInfoSource(_engine) },
            new() { Id = "session-fragments", Source = new DreamSessionFragmentsSource(_engine) },
            new() { Id = "fragment-detail", Source = new FragmentDetailSource(_engine) }
        }
    };

    // ---- 睡眠评估页 ----

    private PageDefinition BuildSleepPage() => new()
    {
        Route = "dream/sleep",
        Meta = new PageMeta { Title = "睡眠评估", Icon = "bi-activity", Group = "做梦引擎", Order = 52 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "sleep-eval", Type = CardType.Status, DataSourceId = "sleep-eval", Title = "睡眠需求评估",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "total_score", Label = "总分", Type = StatusFieldType.Progress },
                        new() { Field = "threshold", Label = "触发阈值" },
                        new() { Field = "factor_idle", Label = "空闲时长 (40)" },
                        new() { Field = "factor_memory", Label = "记忆积压 (30)" },
                        new() { Field = "factor_hints", Label = "复盘标记 (20)" },
                        new() { Field = "factor_time", Label = "距上次 (10)" },
                        new() { Field = "in_window", Label = "深睡窗口", Type = StatusFieldType.Badge },
                        new() { Field = "pending_request", Label = "待审批", Type = StatusFieldType.Badge }
                    },
                    Actions = new()
                    {
                        new() { Id = "approve", Label = "批准深睡", Icon = "bi-check-lg" },
                        new() { Id = "deny", Label = "拒绝", Icon = "bi-x-lg", Danger = true },
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "sleep-config", Type = CardType.Status, DataSourceId = "sleep-config", Title = "睡眠配置",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "daydream_cooldown", Label = "走神冷却" },
                        new() { Field = "nap_idle", Label = "小睡空闲阈值" },
                        new() { Field = "nap_cooldown", Label = "小睡冷却" },
                        new() { Field = "deep_idle", Label = "大睡空闲阈值" },
                        new() { Field = "deep_window", Label = "大睡窗口" },
                        new() { Field = "deep_budget", Label = "大睡Token预算" },
                        new() { Field = "deep_max_minutes", Label = "大睡时间上限" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "sleep-eval", Source = new SleepEvalSource(_engine) },
            new() { Id = "sleep-config", Source = new SleepConfigSource(_engine) }
        }
    };
}

// ======== 数据源 ========

internal class DreamStatusSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DreamStatusSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 3000, 3000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<DreamEngineSpawnCheck>();
        var hasActive = _engine.HasActiveEngine("Dream");
        var snap = check?.GetDreamSnapshot(hasActive);

        if (snap == null || !snap.HasActiveDream)
        {
            var idle = new JsonObject
            {
                ["state"] = "空闲",
                ["level"] = "—",
                ["progress"] = "0",
                ["current_fragment"] = "—",
                ["fragment_elapsed"] = "—",
                ["input_desc"] = "—",
                ["force_flag"] = "否"
            };
            return Task.FromResult(new DataResult { Data = idle });
        }

        var elapsed = snap.CurrentFragmentStartTime.HasValue
            ? $"{(DateTime.Now - snap.CurrentFragmentStartTime.Value).TotalSeconds:F0}s"
            : "—";

        var progressPct = snap.FragmentsTotal > 0
            ? (int)(snap.FragmentsCompleted * 100.0 / snap.FragmentsTotal)
            : 0;

        var data = new JsonObject
        {
            ["state"] = "做梦中",
            ["level"] = snap.PendingLevel.ToString(),
            ["progress"] = $"{progressPct}|{snap.FragmentsCompleted}/{snap.FragmentsTotal}",
            ["current_fragment"] = snap.CurrentFragment ?? "准备中",
            ["fragment_elapsed"] = elapsed,
            ["input_desc"] = snap.CurrentInputDescription ?? "—",
            ["force_flag"] = snap.ForceFlag ? "是" : "否"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var signal = action switch
        {
            "force-deep" => "force-sleep",
            "force-nap" => "force-sleep",
            "force-daydream" => "force-sleep",
            _ => null
        };
        if (signal != null)
        {
            var arg = action switch
            {
                "force-nap" => "nap",
                "force-daydream" => "daydream",
                _ => null
            };
            _engine.EventBus.PublishSignal(signal, arg);
            return Task.FromResult(new ActionResult { Success = true, Message = $"已发送{action}信号" });
        }
        return Task.FromResult(new ActionResult { Success = true });
    }
}

internal class DreamStatsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DreamStatsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 10000, 10000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<DreamEngineSpawnCheck>();
        var snap = check?.GetDreamSnapshot(_engine.HasActiveEngine("Dream"));
        var sessionCount = await _engine.DreamLogs.GetSessionCountAsync();

        var statsPath = Path.Combine(PathConfig.StoragePath, "Dream", "DreamStats.json");
        var stats = DreamStats.Load(statsPath);
        var today = stats.DailyRecords.FirstOrDefault(r => r.Date == DateTime.Now.ToString("yyyy-MM-dd"));

        var data = new JsonObject
        {
            ["total_sessions"] = $"{sessionCount} 次",
            ["last_daydream"] = snap?.LastDaydreamTime?.ToString("HH:mm") ?? "—",
            ["baseline"] = $"{stats.GetBaselineAvg():F0} 条/天",
            ["red_days"] = stats.ConsecutiveRedDays > 0 ? $"{stats.ConsecutiveRedDays} 天" : "0",
            ["today_temp_peak"] = today != null ? $"{today.TempPeak} 条" : "—",
            ["today_processed"] = today != null ? $"{today.Processed} 条" : "—"
        };
        return new DataResult { Data = data };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

// PLACEHOLDER_FRAGMENTS_SOURCE

internal class DreamActiveFragmentsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DreamActiveFragmentsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 3000, 3000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<DreamEngineSpawnCheck>();
        var hasActive = _engine.HasActiveEngine("Dream");
        var snap = check?.GetDreamSnapshot(hasActive);

        var arr = new JsonArray();

        if (hasActive && snap?.CompletedFragments != null)
        {
            // 活跃做梦时用引擎内存中的片段（未入库）
            int seq = 1;
            foreach (var rec in snap.CompletedFragments)
            {
                arr.Add(new JsonObject
                {
                    ["seq"] = seq.ToString(),
                    ["type"] = rec.Type,
                    ["status"] = rec.Success ? "完成" : "失败",
                    ["duration"] = $"{rec.DurationSeconds:F1}s",
                    ["summary"] = (rec.Summary?.Length > 120 ? rec.Summary[..120] + "…" : rec.Summary) ?? ""
                });
                seq++;
            }
        }
        else if (!hasActive)
        {
            // 无活跃做梦时显示最近一次 session 的已入库片段
            var sessions = await _engine.DreamLogs.GetRecentSessionsAsync(1);
            if (sessions.Count > 0)
            {
                var sessionId = sessions[0].Id;
                var fragments = await _engine.DreamLogs.GetFragmentsBySessionAsync(sessionId);
                var seq = 1;
                foreach (var f in fragments)
                {
                    arr.Add(new JsonObject
                    {
                        ["seq"] = seq.ToString(),
                        ["type"] = f.Type,
                        ["status"] = f.Success ? "完成" : "失败",
                        ["duration"] = $"{f.DurationSeconds:F1}s",
                        ["summary"] = f.Summary.Length > 120 ? f.Summary[..120] + "…" : f.Summary,
                        ["_link"] = $"/p/dream/history/{sessionId}"
                    });
                    seq++;
                }
            }
        }

        // 追加当前正在执行的片段
        if (hasActive && snap?.CurrentFragment != null)
        {
            var elapsed = snap.CurrentFragmentStartTime.HasValue
                ? $"{(DateTime.Now - snap.CurrentFragmentStartTime.Value).TotalSeconds:F0}s"
                : "—";
            arr.Add(new JsonObject
            {
                ["seq"] = (snap.FragmentsCompleted + 1).ToString(),
                ["type"] = snap.CurrentFragment,
                ["status"] = "执行中",
                ["duration"] = elapsed,
                ["summary"] = snap.CurrentInputDescription ?? "处理中…"
            });
        }

        return new DataResult { Data = arr, TotalCount = arr.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class DreamSessionsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DreamSessionsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 20;
        var sessions = await _engine.DreamLogs.GetRecentSessionsAsync(100);
        var total = sessions.Count;

        var paged = sessions.Skip((page - 1) * pageSize).Take(pageSize);
        var arr = new JsonArray();
        foreach (var s in paged)
        {
            var duration = s.EndTime > s.StartTime
                ? $"{(s.EndTime - s.StartTime).TotalMinutes:F0}分钟"
                : "进行中";
            arr.Add(new JsonObject
            {
                ["time"] = s.StartTime.ToString("MM-dd HH:mm"),
                ["level"] = s.Level,
                ["fragments"] = s.FragmentsExecuted.ToString(),
                ["duration"] = duration,
                ["interrupted"] = s.WasInterrupted ? "是" : "—",
                ["_link"] = $"/p/dream/history/{s.Id}"
            });
        }
        return new DataResult { Data = arr, TotalCount = total };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

// PLACEHOLDER_SESSION_DETAIL

internal class DreamSessionInfoSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DreamSessionInfoSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        // 活跃 session 时自动刷新
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var route = query?.RouteParams?.GetValueOrDefault("id");
        if (!int.TryParse(route, out var sessionId))
            return new DataResult { Data = new JsonObject { ["level"] = "未知", ["time_range"] = "无效ID", ["fragments"] = "—", ["interrupted"] = "—" } };

        var sessions = await _engine.DreamLogs.GetRecentSessionsAsync(100);
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null)
            return new DataResult { Data = new JsonObject { ["level"] = "未知", ["time_range"] = "未找到", ["fragments"] = "—", ["interrupted"] = "—" } };

        var isActive = session.EndTime <= session.StartTime;
        var timeRange = isActive
            ? $"{session.StartTime:MM-dd HH:mm} ~ 进行中"
            : $"{session.StartTime:MM-dd HH:mm} ~ {session.EndTime:HH:mm}";

        var data = new JsonObject
        {
            ["level"] = session.Level,
            ["time_range"] = timeRange,
            ["fragments"] = $"{session.FragmentsExecuted} 个",
            ["interrupted"] = session.WasInterrupted ? "是" : "否"
        };
        return new DataResult { Data = data };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class DreamSessionFragmentsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DreamSessionFragmentsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var route = query?.RouteParams?.GetValueOrDefault("id");
        if (!int.TryParse(route, out var sessionId))
            return new DataResult { Data = new JsonArray(), TotalCount = 0 };

        var fragments = await _engine.DreamLogs.GetFragmentsBySessionAsync(sessionId);
        var arr = new JsonArray();

        foreach (var f in fragments)
        {
            arr.Add(new JsonObject
            {
                ["_id"] = f.Id.ToString(),
                ["seq"] = (f.SeqIndex + 1).ToString(),
                ["type"] = f.Type,
                ["success"] = f.Success ? "成功" : "失败",
                ["duration"] = $"{f.DurationSeconds:F1}s",
                ["summary"] = f.Summary.Length > 120 ? f.Summary[..120] + "…" : f.Summary
            });
        }

        return new DataResult { Data = arr, TotalCount = fragments.Count };
    }

    private static string FormatDetail(DreamFragmentDetail d)
    {
        return d.Action switch
        {
            "weight_adjust" => $"#{d.MemoryId} {d.OldValue}→{d.NewValue}",
            "link_create" => $"#{d.MemoryId} 关联({d.Note})",
            "combine_derive" => $"合并→#{d.MemoryId}",
            "dedup_merge" => $"去重合并#{d.MemoryId}",
            "dedup_discard" => $"去重丢弃#{d.MemoryId}",
            _ => $"{d.Action} #{d.MemoryId}"
        };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

// PLACEHOLDER_SLEEP_SOURCES

internal class FragmentDetailSource : IDataSource
{
    private readonly MasterEngine _engine;
    public FragmentDetailSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var fragmentIdStr = query?.Extra?["_id"]?.ToString();
        if (!int.TryParse(fragmentIdStr, out var fragmentId))
        {
            return new DataResult
            {
                Data = new JsonObject
                {
                    ["type"] = "—",
                    ["time"] = "点击左侧片段查看详情",
                    ["duration"] = "—",
                    ["success"] = "—",
                    ["summary"] = "—",
                    ["input_memories"] = "—",
                    ["changes"] = "—"
                }
            };
        }

        var fragment = await _engine.DreamLogs.GetFragmentByIdAsync(fragmentId);
        if (fragment == null)
            return new DataResult { Data = new JsonObject { ["type"] = "未找到", ["time"] = "—", ["duration"] = "—", ["success"] = "—", ["summary"] = "—", ["input_memories"] = "—", ["changes"] = "—" } };

        var details = await _engine.DreamLogs.GetDetailsByFragmentAsync(fragmentId);

        var inputMemoriesStr = await FormatInputMemoriesAsync(fragment.InputMemoryIds);

        var changesStr = details.Count > 0
            ? string.Join("\n", details.Select(FormatDetailLine))
            : "无变更记录";

        var data = new JsonObject
        {
            ["type"] = FragmentTypeLabel(fragment.Type),
            ["time"] = fragment.StartTime.ToString("HH:mm:ss"),
            ["duration"] = $"{fragment.DurationSeconds:F1}s",
            ["success"] = fragment.Success ? "成功" : "失败",
            ["summary"] = fragment.Summary,
            ["input_memories"] = inputMemoriesStr,
            ["changes"] = changesStr
        };
        return new DataResult { Data = data };
    }

    private async Task<string> FormatInputMemoriesAsync(string? inputMemoryIds)
    {
        if (string.IsNullOrEmpty(inputMemoryIds))
            return "—";

        var ids = ParseAllIds(inputMemoryIds);
        if (ids.Count == 0) return inputMemoryIds;

        var memEntries = await _engine.Memories.GetByIdsAsync(ids);
        var memMap = memEntries.ToDictionary(m => m.Id, m => m.Content);

        var tempIds = ids.Where(id => !memMap.ContainsKey(id)).ToList();
        if (tempIds.Count > 0)
        {
            var tempEntries = await _engine.TempMemories.GetByIdsAsync(tempIds);
            foreach (var t in tempEntries)
                memMap[t.Id] = $"[临时] {t.Content}";
        }

        var lines = new List<string>();
        foreach (var id in ids)
        {
            var content = memMap.TryGetValue(id, out var c)
                ? (c.Length > 200 ? c[..200] + "…" : c)
                : "(未找到)";
            lines.Add($"#{id}  {content}");
        }
        return string.Join("\n", lines);
    }

    private static List<int> ParseAllIds(string input)
    {
        var ids = new List<int>();
        foreach (var part in input.Split('|', ',', ';', ':'))
        {
            var trimmed = part.Trim();
            if (trimmed == "target" || trimmed == "candidates") continue;
            if (int.TryParse(trimmed, out var id))
                ids.Add(id);
        }
        return ids.Distinct().ToList();
    }

    private static string FragmentTypeLabel(string type) => type switch
    {
        "Consolidation" => "整合",
        "Weight" => "权重",
        "Link" => "关联",
        "Combine" => "组合",
        "Dedup" => "去重",
        _ => type
    };

    private static string FormatDetailLine(DreamFragmentDetail d)
    {
        return d.Action switch
        {
            "weight_adjust" => $"权重调整 #{d.MemoryId}: {d.OldValue} → {d.NewValue}" + (d.Note != null ? $" ({d.Note})" : ""),
            "link_create" => $"建立关联 #{d.MemoryId}: {d.Note}",
            "link_strengthen" => $"强化关联 #{d.MemoryId}: {d.OldValue} → {d.NewValue}",
            "combine_derive" => $"合并衍生 → #{d.MemoryId}" + (d.Note != null ? $" ({d.Note})" : ""),
            "dedup_merge" => $"去重合并 #{d.MemoryId}: {d.Note}",
            "dedup_discard" => $"去重丢弃 #{d.MemoryId}",
            "consolidate_group" => $"整合分组 #{d.MemoryId}: {d.Note}",
            _ => $"{d.Action} #{d.MemoryId}" + (d.Note != null ? $" ({d.Note})" : "")
        };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class DreamDailyStatsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DreamDailyStatsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var statsPath = Path.Combine(PathConfig.StoragePath, "Dream", "DreamStats.json");
        var stats = DreamStats.Load(statsPath);
        var arr = new JsonArray();
        foreach (var r in stats.DailyRecords.OrderByDescending(r => r.Date).Take(7))
        {
            arr.Add(new JsonObject
            {
                ["date"] = r.Date,
                ["temp_peak"] = r.TempPeak.ToString(),
                ["processed"] = r.Processed.ToString(),
                ["undreamed_peak"] = r.UndreamedPeak.ToString()
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class DreamStatsCardsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DreamStatsCardsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var statsPath = Path.Combine(PathConfig.StoragePath, "Dream", "DreamStats.json");
        var stats = DreamStats.Load(statsPath);
        var data = new JsonObject
        {
            ["baseline_avg"] = $"{stats.Baseline.AvgDailyTempIntake:F1} 条/天",
            ["baseline_update"] = string.IsNullOrEmpty(stats.Baseline.LastUpdate) ? "—" : stats.Baseline.LastUpdate,
            ["red_days"] = stats.ConsecutiveRedDays > 0 ? $"{stats.ConsecutiveRedDays} 天" : "0"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class SleepEvalSource : IDataSource
{
    private readonly MasterEngine _engine;
    public SleepEvalSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 10000, 10000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<DreamEngineSpawnCheck>();
        var config = check?.GetConfig() ?? DreamConfig.Load(
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamConfig.json"));

        // 复现 4 因子评分
        float factorIdle = 0f, factorMemory = 0f, factorHints = 0f, factorTime = 0f;

        // 因子1：空闲时长
        if (_engine.IsIdle)
        {
            var idleMinutes = _engine.IdleDuration.TotalMinutes;
            factorIdle = Math.Min(40f, (float)(idleMinutes / 30f * 40f));
        }

        // 因子2：记忆积压
        var recentMemories = await _engine.Memories.GetRecentAsync(100);
        var undreamedCount = recentMemories.Count(m => m.LastDreamTime == null);
        factorMemory = Math.Min(30f, undreamedCount / 50f * 30f);

        // 因子3：复盘标记
        var unprocessedHints = await _engine.ReviewHints.GetUnprocessedAsync();
        factorHints = Math.Min(20f, unprocessedHints.Count / 10f * 20f);

        // 因子4：距上次睡觉
        var statsPath = Path.Combine(PathConfig.StoragePath, "Dream", "DreamStats.json");
        var stats = DreamStats.Load(statsPath);
        var lastSleepRecord = stats.DailyRecords
            .Where(r => r.Processed > 0)
            .OrderByDescending(r => r.Date)
            .FirstOrDefault();
        if (lastSleepRecord != null && DateTime.TryParse(lastSleepRecord.Date, out var lastDate))
        {
            var hoursSince = (DateTime.Now - lastDate).TotalHours;
            factorTime = Math.Min(10f, (float)(hoursSince / 24f * 10f));
        }

        var totalScore = factorIdle + factorMemory + factorHints + factorTime;

        // 睡眠请求状态
        var sysEngine = _engine.GetSystemEngine();
        var sysSnapshot = sysEngine?.GetSnapshot();
        var hasPending = sysSnapshot?.HasPendingSleepRequest ?? false;
        var pendingStr = hasPending
            ? $"是 (分数:{sysSnapshot!.SleepScore:F0}, {(DateTime.Now - sysSnapshot.SleepRequestTime!.Value).TotalMinutes:F0}分钟前)"
            : "无";

        var data = new JsonObject
        {
            ["total_score"] = $"{(int)totalScore}|{(int)totalScore}/100",
            ["threshold"] = "60 分",
            ["factor_idle"] = $"{factorIdle:F0} 分",
            ["factor_memory"] = $"{factorMemory:F0} 分 ({undreamedCount}条未处理)",
            ["factor_hints"] = $"{factorHints:F0} 分 ({unprocessedHints.Count}条)",
            ["factor_time"] = $"{factorTime:F0} 分",
            ["in_window"] = config.IsInDeepSleepWindow() ? "是" : "否",
            ["pending_request"] = pendingStr,
            ["_disabled_actions"] = hasPending ? null : new JsonArray("approve", "deny")
        };
        return new DataResult { Data = data };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        return action switch
        {
            "approve" => ApproveRequest(),
            "deny" => DenyRequest(),
            _ => Task.FromResult(new ActionResult { Success = true }),
        };
    }

    private Task<ActionResult> ApproveRequest()
    {
        var snapshot = _engine.GetSystemEngine()?.GetSnapshot();
        if (snapshot?.HasPendingSleepRequest != true)
            return Task.FromResult(new ActionResult { Success = false, Message = "没有待审批的睡眠请求" });
        _engine.EventBus.PublishSignal("sleep-approve", snapshot.SleepRequestId!);
        return Task.FromResult(new ActionResult { Success = true, Message = "已批准深睡请求" });
    }

    private Task<ActionResult> DenyRequest()
    {
        var snapshot = _engine.GetSystemEngine()?.GetSnapshot();
        if (snapshot?.HasPendingSleepRequest != true)
            return Task.FromResult(new ActionResult { Success = false, Message = "没有待审批的睡眠请求" });
        _engine.EventBus.PublishSignal("sleep-deny", snapshot.SleepRequestId!);
        return Task.FromResult(new ActionResult { Success = true, Message = "已拒绝深睡请求" });
    }
}

internal class SleepConfigSource : IDataSource
{
    private readonly MasterEngine _engine;
    public SleepConfigSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<DreamEngineSpawnCheck>();
        var config = check?.GetConfig() ?? DreamConfig.Load(
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamConfig.json"));

        var data = new JsonObject
        {
            ["daydream_cooldown"] = $"{config.DaydreamCooldown}s",
            ["nap_idle"] = $"{config.NapIdleThreshold}s ({config.NapIdleThreshold / 60}分钟)",
            ["nap_cooldown"] = $"{config.NapCooldown}s ({config.NapCooldown / 60}分钟)",
            ["deep_idle"] = $"{config.DeepSleepIdleThreshold}s ({config.DeepSleepIdleThreshold / 60}分钟)",
            ["deep_window"] = $"{config.DeepSleepTimeStart} ~ {config.DeepSleepTimeEnd}",
            ["deep_budget"] = $"{config.DeepSleepTokenBudget:#,0} tokens",
            ["deep_max_minutes"] = $"{config.DeepSleepMaxMinutes} 分钟"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}
