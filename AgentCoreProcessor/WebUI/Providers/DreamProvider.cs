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
        Meta = new PageMeta { Title = "状态", Icon = "bi-moon-stars", Group = "做梦", Order = 30 },
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
        Meta = new PageMeta { Title = "历史", Icon = "bi-clock-history", Group = "做梦", Order = 31 },
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
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "dream-sessions", Source = new DreamSessionsSource(_engine) }
        }
    };

    // ---- 会话详情页（隐藏导航） ----

    private PageDefinition BuildSessionDetailPage() => new()
    {
        Route = "dream/history/{id}",
        Meta = new PageMeta { Title = "会话详情", Icon = "bi-list-nested", Group = "做梦", ShowInNav = false },
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
                Layout = new CardLayout { PreferredCols = 12 }
            },
            new()
            {
                Id = "session-fragments", Type = CardType.Table, DataSourceId = "session-fragments", Title = "片段列表",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "seq", Header = "#", Width = "40px" },
                        new() { Field = "type", Header = "类型", Width = "100px", Format = ColumnFormat.Badge },
                        new() { Field = "success", Header = "结果", Width = "60px", Format = ColumnFormat.Badge },
                        new() { Field = "duration", Header = "耗时", Width = "70px" },
                        new() { Field = "summary", Header = "摘要" },
                        new() { Field = "details", Header = "变更", Width = "300px" }
                    },
                    DefaultPageSize = 50
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "session-info", Source = new DreamSessionInfoSource(_engine) },
            new() { Id = "session-fragments", Source = new DreamSessionFragmentsSource(_engine) }
        }
    };

    // ---- 睡眠评估页 ----

    private PageDefinition BuildSleepPage() => new()
    {
        Route = "dream/sleep",
        Meta = new PageMeta { Title = "睡眠评估", Icon = "bi-activity", Group = "做梦", Order = 32 },
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
        => Task.FromResult(new ActionResult { Success = true });
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

        // 如果有活跃 session，从数据库拿已完成片段 + 实时正在执行的
        // 如果没有活跃 session，显示最近一次 session 的片段
        var sessions = await _engine.DreamLogs.GetRecentSessionsAsync(1);
        int? sessionId = null;

        if (hasActive && sessions.Count > 0)
            sessionId = sessions[0].Id;
        else if (!hasActive && sessions.Count > 0)
            sessionId = sessions[0].Id;

        var arr = new JsonArray();

        if (sessionId.HasValue)
        {
            var fragments = await _engine.DreamLogs.GetFragmentsBySessionAsync(sessionId.Value);
            foreach (var f in fragments)
            {
                arr.Add(new JsonObject
                {
                    ["seq"] = (f.SeqIndex + 1).ToString(),
                    ["type"] = f.Type,
                    ["status"] = f.Success ? "完成" : "失败",
                    ["duration"] = $"{f.DurationSeconds:F1}s",
                    ["summary"] = f.Summary.Length > 120 ? f.Summary[..120] + "…" : f.Summary
                });
            }
        }

        // 追加当前正在执行的片段（未入库）
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
            var details = await _engine.DreamLogs.GetDetailsByFragmentAsync(f.Id);
            var detailStr = details.Count > 0
                ? string.Join("; ", details.Take(5).Select(FormatDetail))
                  + (details.Count > 5 ? $" (+{details.Count - 5})" : "")
                : "—";

            arr.Add(new JsonObject
            {
                ["seq"] = (f.SeqIndex + 1).ToString(),
                ["type"] = f.Type,
                ["success"] = f.Success ? "成功" : "失败",
                ["duration"] = $"{f.DurationSeconds:F1}s",
                ["summary"] = f.Summary.Length > 100 ? f.Summary[..100] + "…" : f.Summary,
                ["details"] = detailStr
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
            ["pending_request"] = pendingStr
        };
        return new DataResult { Data = data };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
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
