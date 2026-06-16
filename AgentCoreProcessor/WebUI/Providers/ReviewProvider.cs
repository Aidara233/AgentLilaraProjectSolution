using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

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
        BuildStatusPage(),
        BuildConfigPage(),
        BuildHistoryPage(),
        BuildSessionPage(),
        BuildContextPage(),
        BuildChangesPage()
    };

    // ======== 状态页 /p/review ========

    private PageDefinition BuildStatusPage() => new()
    {
        Route = "review",
        Meta = new PageMeta { Title = "状态", Icon = "bi-journal-check", Group = "复盘引擎", Order = 60 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "review-engine-status", Type = CardType.Status, DataSourceId = "review-status", Title = "复盘引擎",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "state", Label = "状态", Type = StatusFieldType.Indicator },
                        new() { Field = "seed", Label = "种子", Type = StatusFieldType.Badge },
                        new() { Field = "tokens", Label = "Token" },
                        new() { Field = "cursor", Label = "游标" },
                        new() { Field = "eval_buffer", Label = "评价缓冲" },
                        new() { Field = "scope", Label = "涉及范围" },
                        new() { Field = "thinking_notes", Label = "思考笔记" },
                        new() { Field = "resume_info", Label = "未完成进度" }
                    },
                    Actions = new()
                    {
                        new() { Id = "review-new-beacon", Label = "发起信标复盘", Icon = "bi-flag" },
                        new() { Id = "review-new-candidate", Label = "发起候选人复盘", Icon = "bi-person-check" },
                        new() { Id = "review-resume", Label = "继续上次", Icon = "bi-arrow-repeat" },
                        new() { Id = "review-discard-progress", Label = "丢弃进度", Icon = "bi-trash" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "review-eval-buffer", Type = CardType.Table, DataSourceId = "review-eval-buffer", Title = "评价缓冲",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "target", Header = "目标", Width = "120px" },
                        new() { Field = "dimension", Header = "维度", Width = "100px" },
                        new() { Field = "ratings", Header = "评分", Width = "80px" },
                        new() { Field = "count", Header = "次数", Width = "60px" }
                    },
                    DefaultPageSize = 50
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "review-recent-sessions", Type = CardType.Table, DataSourceId = "review-recent-sessions", Title = "最近会话",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "time", Header = "时间", Width = "140px" },
                        new() { Field = "seed", Header = "种子", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "stop_reason", Header = "结束", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "tokens", Header = "Token", Width = "80px" },
                        new() { Field = "rounds", Header = "轮次", Width = "60px" },
                        new() { Field = "evals", Header = "评价", Width = "60px" }
                    },
                    DefaultPageSize = 10
                },
                Layout = new CardLayout { PreferredCols = 6 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "review-status", Source = new ReviewStatusSource(_engine) },
            new() { Id = "review-eval-buffer", Source = new ReviewEvalBufferSource(_engine) },
            new() { Id = "review-recent-sessions", Source = new ReviewRecentSessionsSource(_engine) }
        }
    };

    // ======== 配置页 /p/review/config ========

    private PageDefinition BuildConfigPage() => new()
    {
        Route = "review/config",
        Meta = new PageMeta { Title = "配置", Icon = "bi-sliders", Group = "复盘引擎", Order = 59 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "review-config", Type = CardType.Form, DataSourceId = "review-config", Title = "复盘参数",
                Schema = new FormSchema
                {
                    ShowSubmit = true,
                    Fields = new()
                    {
                        new() { Field = "Enabled", Label = "启用复盘", Type = FormFieldType.Toggle },
                        new() { Field = "TokenBudget", Label = "Token 预算", Type = FormFieldType.Number },
                        new() { Field = "ReserveBudget", Label = "备用预算", Type = FormFieldType.Number },
                        new() { Field = "CompressionThreshold", Label = "压缩阈值", Type = FormFieldType.Number },
                        new() { Field = "MaxNavigationRounds", Label = "空转轮次上限", Type = FormFieldType.Number },
                        new() { Field = "EvaluationRate", Label = "评价应用率", Type = FormFieldType.Number },
                        new() { Field = "PersonFloor", Label = "人物维度下限", Type = FormFieldType.Number },
                        new() { Field = "PersonCeiling", Label = "人物维度上限", Type = FormFieldType.Number },
                        new() { Field = "PersonBaseline", Label = "人物维度基线", Type = FormFieldType.Number },
                        new() { Field = "StrangerMinMessages", Label = "Stranger 最低消息数", Type = FormFieldType.Number },
                        new() { Field = "UnderstandingMinMemories", Label = "Understanding 最低记忆数", Type = FormFieldType.Number },
                        new() { Field = "UnderstandingMinDays", Label = "Understanding 最低天数", Type = FormFieldType.Number },
                        new() { Field = "UnderstandingAnyDimension", Label = "Understanding 任一维度最低分", Type = FormFieldType.Number },
                        new() { Field = "FamiliarityMinDays", Label = "Familiarity 最低天数", Type = FormFieldType.Number },
                        new() { Field = "FamiliarityMajorityDimension", Label = "Familiarity 多数维度最低分", Type = FormFieldType.Number },
                        new() { Field = "TrustMinDays", Label = "Trust 最低天数", Type = FormFieldType.Number },
                        new() { Field = "TrustMinReviewCount", Label = "Trust 最低 Review 次数", Type = FormFieldType.Number },
                        new() { Field = "TrustAllDimensions", Label = "Trust 全部维度最低分", Type = FormFieldType.Number },
                    }
                },
                Layout = new CardLayout { PreferredCols = 6, GridColumnStart = 1 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "review-config", Source = new ReviewConfigSource() }
        }
    };

    // ======== 历史页 /p/review/history ========

    private PageDefinition BuildHistoryPage() => new()
    {
        Route = "review/history",
        Meta = new PageMeta { Title = "历史", Icon = "bi-clock-history", Group = "复盘引擎", Order = 61 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "review-sessions", Type = CardType.Table, DataSourceId = "review-sessions", Title = "复盘会话",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "time", Header = "时间", Width = "140px" },
                        new() { Field = "seed", Header = "种子", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "stop_reason", Header = "结束原因", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "tokens", Header = "Token", Width = "80px" },
                        new() { Field = "rounds", Header = "轮次", Width = "60px" },
                        new() { Field = "evals", Header = "评价", Width = "60px" },
                        new() { Field = "scope", Header = "涉及" }
                    },
                    DefaultPageSize = 20
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "review-sessions", Source = new ReviewSessionsSource(_engine) }
        }
    };

    // ======== 会话概括 /p/review/session/{id} ========

    private PageDefinition BuildSessionPage() => new()
    {
        Route = "review/session/{id}",
        Meta = new PageMeta { Title = "会话概括", Icon = "bi-list-check", Group = "复盘引擎", ShowInNav = false },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "session-stats", Type = CardType.Status, DataSourceId = "session-stats", Title = "会话信息",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "seed", Label = "种子", Type = StatusFieldType.Badge },
                        new() { Field = "stop_reason", Label = "结束原因", Type = StatusFieldType.Badge },
                        new() { Field = "time_range", Label = "时间" },
                        new() { Field = "tokens", Label = "Token用量" },
                        new() { Field = "rounds", Label = "执行轮次" },
                        new() { Field = "evals", Label = "评价数" },
                        new() { Field = "channels", Label = "涉及频道" },
                        new() { Field = "persons", Label = "涉及人物" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            },
            new()
            {
                Id = "session-thinking-notes", Type = CardType.Status, DataSourceId = "session-thinking-notes", Title = "思考笔记",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "notes", Label = "" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            },
            new()
            {
                Id = "session-actions", Type = CardType.Table, DataSourceId = "session-actions", Title = "工具调用",
                LinkEvent = "action-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "seq", Header = "#", Width = "40px" },
                        new() { Field = "time", Header = "时间", Width = "100px" },
                        new() { Field = "action", Header = "操作", Width = "140px", Format = ColumnFormat.Badge },
                        new() { Field = "summary", Header = "摘要" }
                    },
                    DefaultPageSize = 50
                },
                Layout = new CardLayout { PreferredCols = 12 }
            },
            new()
            {
                Id = "session-raw-evals", Type = CardType.Table, DataSourceId = "session-raw-evals", Title = "原始评价记录",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "target", Header = "目标", Width = "120px" },
                        new() { Field = "dimension", Header = "维度", Width = "100px" },
                        new() { Field = "rating", Header = "评分", Width = "60px" }
                    },
                    DefaultPageSize = 50
                },
                Layout = new CardLayout { PreferredCols = 6 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "session-stats", Source = new ReviewSessionStatsSource(_engine) },
            new() { Id = "session-thinking-notes", Source = new ReviewThinkingNotesSource(_engine) },
            new() { Id = "session-actions", Source = new ReviewActionsSource(_engine) },
            new() { Id = "session-raw-evals", Source = new ReviewRawEvaluationsSource(_engine) }
        }
    };

    // ======== 原始上下文 /p/review/session/{id}/context ========

    private PageDefinition BuildContextPage() => new()
    {
        Route = "review/session/{id}/context",
        Meta = new PageMeta { Title = "原始上下文", Icon = "bi-code-slash", Group = "复盘引擎", ShowInNav = false },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "session-context", Type = CardType.Stream, DataSourceId = "session-context", Title = "Agent 对话历史",
                Schema = new StreamSchema { MaxLines = 500, ShowFilter = true }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "session-context", Source = new ReviewContextSource(_engine) }
        }
    };

    // ======== 改动记录 /p/review/session/{id}/changes ========

    private PageDefinition BuildChangesPage() => new()
    {
        Route = "review/session/{id}/changes",
        Meta = new PageMeta { Title = "改动记录", Icon = "bi-pencil-square", Group = "复盘引擎", ShowInNav = false },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "session-actions-detailed", Type = CardType.Table, DataSourceId = "session-actions-detailed", Title = "操作记录",
                LinkEvent = "action-detail-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "seq", Header = "#", Width = "40px" },
                        new() { Field = "time", Header = "时间", Width = "100px" },
                        new() { Field = "action", Header = "操作", Width = "140px", Format = ColumnFormat.Badge },
                        new() { Field = "summary", Header = "摘要" }
                    },
                    DefaultPageSize = 50
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "action-detail-card", Type = CardType.Status, DataSourceId = "action-detail", Title = "操作详情",
                ListenEvent = "action-detail-selected",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "action", Label = "操作", Type = StatusFieldType.Badge },
                        new() { Field = "time", Label = "时间" },
                        new() { Field = "summary", Label = "摘要" },
                        new() { Field = "detail", Label = "详情" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "session-actions-detailed", Source = new ReviewActionsDetailedSource(_engine) },
            new() { Id = "action-detail", Source = new ReviewActionDetailSource(_engine) }
        }
    };
}

// ======== 数据源 ========

/// <summary>
/// 复盘引擎状态（含 resume 支持和操作按钮）
/// </summary>
internal class ReviewStatusSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ReviewStatusSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    private static string ProgressPath =>
        Path.Combine(PathConfig.StoragePath, "Dream", "ReviewProgress.json");

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var review = _engine.GetActiveEnginesSnapshot().OfType<ReviewEngine>().FirstOrDefault();
        if (review == null)
        {
            var progress = ReviewProgress.Load(ProgressPath);
            var hasProgress = progress.SavedAt != null;
            var resumeInfo = "无未完成的复盘";

            if (hasProgress)
            {
                var bufferCnt = progress.EvaluationBuffer.Count;
                resumeInfo = $"上次保存于 {progress.SavedAt:MM-dd HH:mm} | {bufferCnt} 条待应用";
                if (progress.ResumeCount > 0)
                    resumeInfo += $" | 已恢复 {progress.ResumeCount} 次";
            }

            return Task.FromResult(new DataResult
            {
                Data = new JsonObject
                {
                    ["state"] = hasProgress ? "未完成" : "空闲",
                    ["seed"] = "—",
                    ["tokens"] = "—",
                    ["cursor"] = "—",
                    ["eval_buffer"] = "—",
                    ["scope"] = "—",
                    ["thinking_notes"] = "—",
                    ["resume_info"] = resumeInfo,
                    ["_has_progress"] = hasProgress ? "1" : "0"
                }
            });
        }

        var tokenStr = (review.TokensUsed / 1000.0).ToString("F0") + "k";
        var bufferCount = review.EvaluationBuffer.Count;
        var channelCount = review.ChannelsVisited.Count;
        var personCount = review.PersonsEncountered.Count;

        var data = new JsonObject
        {
            ["state"] = "运行中",
            ["seed"] = "信标/随机/续接",
            ["tokens"] = tokenStr,
            ["cursor"] = $"ch{review.CursorChannelId} msg{review.CursorMessageId}",
            ["eval_buffer"] = bufferCount > 0 ? $"{bufferCount} 条待应用" : "空",
            ["scope"] = $"{channelCount}频道 {personCount}人",
            ["thinking_notes"] = string.IsNullOrEmpty(review.ThinkingNotes) ? "—" : $"{review.ThinkingNotes.Length} 字符",
            ["resume_info"] = "运行中"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        switch (action)
        {
            case "review-new-beacon":
                _engine.EventBus.PublishSignal("force-review:beacon", "");
                return Task.FromResult(new ActionResult { Success = true, Message = "已触发信标复盘" });
            case "review-new-candidate":
                _engine.EventBus.PublishSignal("force-review:candidate", "");
                return Task.FromResult(new ActionResult { Success = true, Message = "已触发候选人复盘" });
            case "review-resume":
                _engine.EventBus.PublishSignal("force-review:resume", "");
                return Task.FromResult(new ActionResult { Success = true, Message = "已触发继续复盘" });
            case "review-discard-progress":
                if (File.Exists(ProgressPath))
                    File.Delete(ProgressPath);
                return Task.FromResult(new ActionResult { Success = true, Message = "已丢弃旧进度" });
        }
        return Task.FromResult(new ActionResult { Success = true });
    }
}

/// <summary>
/// 评价缓冲表（空闲时从文件读取，活跃时从引擎读取）
/// </summary>
internal class ReviewEvalBufferSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ReviewEvalBufferSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    private static string ProgressPath =>
        Path.Combine(PathConfig.StoragePath, "Dream", "ReviewProgress.json");

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        List<EvaluationBufferEntry> buffer;
        var review = _engine.GetActiveEnginesSnapshot().OfType<ReviewEngine>().FirstOrDefault();

        if (review != null)
        {
            buffer = review.EvaluationBuffer.ToList();
        }
        else
        {
            var progress = ReviewProgress.Load(ProgressPath);
            buffer = progress.EvaluationBuffer;
        }

        var groups = buffer
            .GroupBy(e => new { e.TargetType, e.TargetId, e.Dimension })
            .Select(g => new JsonObject
            {
                ["target"] = $"{g.Key.TargetType}#{g.Key.TargetId}",
                ["dimension"] = g.Key.Dimension,
                ["ratings"] = string.Join(",", g.Select(e => e.Rating)),
                ["count"] = g.Count().ToString()
            })
            .ToList();

        var arr = new JsonArray();
        foreach (var obj in groups)
            arr.Add(obj);

        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

/// <summary>
/// 最近复盘会话（状态页用）
/// </summary>
internal class ReviewRecentSessionsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ReviewRecentSessionsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var sessions = await _engine.ReviewLogs.GetRecentSessionsAsync(10);
        var arr = new JsonArray();
        foreach (var s in sessions)
        {
            arr.Add(FormatSessionRow(s));
        }
        return new DataResult { Data = arr, TotalCount = arr.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });

    internal static JsonObject FormatSessionRow(ReviewSession s)
    {
        return new JsonObject
        {
            ["time"] = s.StartTime.ToString("MM-dd HH:mm"),
            ["seed"] = s.SeedType,
            ["stop_reason"] = s.StopReason,
            ["tokens"] = $"{s.TokensUsed / 1000}k",
            ["rounds"] = s.RoundsExecuted.ToString(),
            ["evals"] = s.EvaluationCount.ToString(),
            ["_link"] = $"/p/review/session/{s.Id}"
        };
    }
}

/// <summary>
/// 全量复盘会话列表（历史页）
/// </summary>
internal class ReviewSessionsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ReviewSessionsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 20;
        var sessions = await _engine.ReviewLogs.GetRecentSessionsAsync(200);
        var total = sessions.Count;

        var paged = sessions.Skip((page - 1) * pageSize).Take(pageSize);
        var arr = new JsonArray();
        foreach (var s in paged)
        {
            var scope = $"{s.ChannelsVisited} / {s.PersonsEncountered}";
            try
            {
                var chArr = System.Text.Json.JsonSerializer.Deserialize<int[]>(s.ChannelsVisited);
                var pArr = System.Text.Json.JsonSerializer.Deserialize<int[]>(s.PersonsEncountered);
                var chCount = chArr?.Length ?? 0;
                var pCount = pArr?.Length ?? 0;
                scope = $"{chCount}频道 {pCount}人";
            }
            catch { }

            arr.Add(new JsonObject
            {
                ["time"] = s.StartTime.ToString("MM-dd HH:mm"),
                ["seed"] = s.SeedType,
                ["stop_reason"] = s.StopReason,
                ["tokens"] = $"{s.TokensUsed / 1000}k",
                ["rounds"] = s.RoundsExecuted.ToString(),
                ["evals"] = s.EvaluationCount.ToString(),
                ["scope"] = scope,
                ["_link"] = $"/p/review/session/{s.Id}"
            });
        }
        return new DataResult { Data = arr, TotalCount = total };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

/// <summary>
/// 会话统计信息（兼容活跃/历史两种数据源）
/// </summary>
internal class ReviewSessionStatsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ReviewSessionStatsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 10000, 10000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var route = query?.RouteParams?.GetValueOrDefault("id");
        var isActive = route == null || route == "0";

        if (isActive)
        {
            // 活跃引擎数据
            var review = _engine.GetActiveEnginesSnapshot().OfType<ReviewEngine>().FirstOrDefault();
            if (review == null)
                return new DataResult { Data = NoDataObj() };

            var ctx = review.GetContextSnapshot();
            var chCount = review.ChannelsVisited.Count;
            var pCount = review.PersonsEncountered.Count;

            return new DataResult
            {
                Data = new JsonObject
                {
                    ["seed"] = "活跃中",
                    ["stop_reason"] = "—",
                    ["time_range"] = "运行中…",
                    ["tokens"] = $"已用 {(review.TokensUsed / 1000.0):F0}k | 上下文 {(ctx?.EstimatedTokens ?? 0) / 1000}k",
                    ["rounds"] = ctx?.TotalRounds.ToString() ?? "—",
                    ["evals"] = review.EvaluationBuffer.Count.ToString(),
                    ["channels"] = $"{chCount} 个",
                    ["persons"] = $"{pCount} 人"
                }
            };
        }

        // 历史数据
        if (!int.TryParse(route, out var sessionId))
            return new DataResult { Data = NoDataObj() };

        var sessions = await _engine.ReviewLogs.GetRecentSessionsAsync(200);
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null)
            return new DataResult { Data = NoDataObj() };

        var timeRange = session.EndTime > session.StartTime
            ? $"{session.StartTime:MM-dd HH:mm} ~ {session.EndTime:HH:mm}"
            : $"{session.StartTime:MM-dd HH:mm} ~ 进行中";

        var scope = $"?/?";
        try
        {
            var chArr = System.Text.Json.JsonSerializer.Deserialize<int[]>(session.ChannelsVisited);
            var pArr = System.Text.Json.JsonSerializer.Deserialize<int[]>(session.PersonsEncountered);
            scope = $"{chArr?.Length ?? 0}频道 {pArr?.Length ?? 0}人";
        }
        catch { }

        return new DataResult
        {
            Data = new JsonObject
            {
                ["seed"] = session.SeedType,
                ["stop_reason"] = session.StopReason,
                ["time_range"] = timeRange,
                ["tokens"] = $"{(session.TokensUsed / 1000)}k",
                ["rounds"] = session.RoundsExecuted.ToString(),
                ["evals"] = session.EvaluationCount.ToString(),
                ["channels"] = scope.Split(' ')[0],
                ["persons"] = scope.Split(' ').Length > 1 ? scope.Split(' ')[1] : "?"
            }
        };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });

    private static JsonObject NoDataObj() => new()
    {
        ["seed"] = "—",
        ["stop_reason"] = "—",
        ["time_range"] = "无数据",
        ["tokens"] = "—",
        ["rounds"] = "—",
        ["evals"] = "—",
        ["channels"] = "—",
        ["persons"] = "—"
    };
}

/// <summary>
/// 思考笔记（活跃/历史）
/// </summary>
internal class ReviewThinkingNotesSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ReviewThinkingNotesSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 10000, 10000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var route = query?.RouteParams?.GetValueOrDefault("id");
        var isActive = route == null || route == "0";

        string notes;
        if (isActive)
        {
            var review = _engine.GetActiveEnginesSnapshot().OfType<ReviewEngine>().FirstOrDefault();
            notes = review?.ThinkingNotes ?? "";
        }
        else if (int.TryParse(route, out var sessionId))
        {
            var sessions = await _engine.ReviewLogs.GetRecentSessionsAsync(200);
            var session = sessions.FirstOrDefault(s => s.Id == sessionId);
            notes = session?.ThinkingNotes ?? "";
        }
        else
        {
            notes = "";
        }

        return new DataResult
        {
            Data = new JsonObject
            {
                ["notes"] = string.IsNullOrEmpty(notes) ? "（无）" : notes
            }
        };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

/// <summary>
/// 工具调用摘要（活跃从上下文提取，历史从 DB）
/// </summary>
internal class ReviewActionsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ReviewActionsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 10000, 10000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var route = query?.RouteParams?.GetValueOrDefault("id");
        var isActive = route == null || route == "0";

        if (isActive)
        {
            // 从活跃引擎的 Agent 上下文提取工具调用
            var review = _engine.GetActiveEnginesSnapshot().OfType<ReviewEngine>().FirstOrDefault();
            if (review == null)
                return new DataResult { Data = new JsonArray(), TotalCount = 0 };

            var ctx = review.GetContextSnapshot();
            if (ctx == null)
                return new DataResult { Data = new JsonArray(), TotalCount = 0 };

            var arr = new JsonArray();
            int seq = 0;
            string lastThinking = "";
            foreach (var msg in ctx.Messages)
            {
                if (msg.Parts == null) continue;
                foreach (var p in msg.Parts)
                {
                    if (p.ToolName != null)
                    {
                        arr.Add(new JsonObject
                        {
                            ["seq"] = (++seq).ToString(),
                            ["time"] = "",
                            ["action"] = p.ToolName,
                            ["summary"] = Truncate(p.ToolInput ?? "", 200),
                            ["_thinking"] = Truncate(lastThinking, 300)
                        });
                        lastThinking = "";
                    }
                    else if (p.Text != null)
                    {
                        lastThinking = p.Text;
                    }
                }
            }
            return new DataResult { Data = arr, TotalCount = arr.Count };
        }

        // 历史数据：从 ReviewActions 表
        if (!int.TryParse(route, out var sessionId))
            return new DataResult { Data = new JsonArray(), TotalCount = 0 };

        var actions = await _engine.ReviewLogs.GetActionsBySessionAsync(sessionId);
        var arr2 = new JsonArray();
        foreach (var a in actions)
        {
            arr2.Add(new JsonObject
            {
                ["_id"] = a.Id.ToString(),
                ["seq"] = (a.SeqIndex + 1).ToString(),
                ["time"] = a.Time.ToString("HH:mm:ss"),
                ["action"] = a.ActionType,
                ["summary"] = a.Summary.Length > 200 ? a.Summary[..200] + "…" : a.Summary
            });
        }
        return new DataResult { Data = arr2, TotalCount = arr2.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "…" : s;
}

/// <summary>
/// 原始上下文（仅活跃时可见）
/// </summary>
internal class ReviewContextSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ReviewContextSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 10000, 10000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var route = query?.RouteParams?.GetValueOrDefault("id");
        var isActive = route == null || route == "0";

        if (!isActive)
            return Task.FromResult(new DataResult { Data = new JsonArray("上下文仅在复盘运行期间可用") });

        var review = _engine.GetActiveEnginesSnapshot().OfType<ReviewEngine>().FirstOrDefault();
        var snap = review?.GetContextSnapshot();
        if (snap == null)
            return Task.FromResult(new DataResult { Data = new JsonArray("Agent 未激活") });

        var arr = new JsonArray();

        int index = 0;
        foreach (var msg in snap.Messages)
        {
            var prefix = index < snap.ConversationOffset ? "[框架]" : $"[{index - snap.ConversationOffset}]";
            var role = msg.Role.ToUpper();
            var tokens = msg.EstimatedTokens > 0 ? $" ({msg.EstimatedTokens}t)" : "";

            if (msg.Parts != null && msg.Parts.Count > 0)
            {
                foreach (var p in msg.Parts)
                {
                    if (p.ToolName != null)
                        arr.Add($"{prefix} {role}{tokens} ⚙ {p.ToolName} → {Truncate(p.ToolInput, 150)}");
                    else if (p.IsError == true)
                        arr.Add($"{prefix} {role}{tokens} ❌ {Truncate(p.Text, 200)}");
                    else if (p.Text != null && p.Type == "tool_use")
                        arr.Add($"{prefix} {role}{tokens} [tool_use]");
                    else if (p.Text != null)
                        arr.Add($"{prefix} {role}{tokens} 💭 {Truncate(p.Text, 200)}");
                }
            }
            else if (!string.IsNullOrEmpty(msg.Content))
            {
                arr.Add($"{prefix} {role}{tokens} {Truncate(msg.Content, 300)}");
            }

            index++;
        }

        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "…" : s;
}

/// <summary>
/// 改动记录：操作时间线 + 详情联动
/// </summary>
internal class ReviewActionsDetailedSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ReviewActionsDetailedSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var route = query?.RouteParams?.GetValueOrDefault("id");
        var isActive = route == null || route == "0";

        if (isActive)
        {
            // 活跃时从上下文提取（和 ReviewActionsSource 逻辑相同但带 _id）
            var review = _engine.GetActiveEnginesSnapshot().OfType<ReviewEngine>().FirstOrDefault();
            if (review == null)
                return new DataResult { Data = new JsonArray(), TotalCount = 0 };

            var ctx = review.GetContextSnapshot();
            if (ctx == null)
                return new DataResult { Data = new JsonArray(), TotalCount = 0 };

            var arr = new JsonArray();
            int seq = 0;
            foreach (var msg in ctx.Messages)
            {
                if (msg.Parts == null) continue;
                foreach (var p in msg.Parts)
                {
                    if (p.ToolName != null)
                    {
                        arr.Add(new JsonObject
                        {
                            ["_id"] = (++seq).ToString(),
                            ["seq"] = seq.ToString(),
                            ["time"] = "",
                            ["action"] = p.ToolName,
                            ["summary"] = Truncate(p.ToolInput ?? "", 200)
                        });
                    }
                }
            }
            return new DataResult { Data = arr, TotalCount = arr.Count };
        }

        // 历史数据
        if (!int.TryParse(route, out var sessionId))
            return new DataResult { Data = new JsonArray(), TotalCount = 0 };

        var actions = await _engine.ReviewLogs.GetActionsBySessionAsync(sessionId);
        var arr2 = new JsonArray();
        foreach (var a in actions)
        {
            arr2.Add(new JsonObject
            {
                ["_id"] = a.Id.ToString(),
                ["seq"] = (a.SeqIndex + 1).ToString(),
                ["time"] = a.Time.ToString("HH:mm:ss"),
                ["action"] = a.ActionType,
                ["summary"] = a.Summary.Length > 200 ? a.Summary[..200] + "…" : a.Summary
            });
        }
        return new DataResult { Data = arr2, TotalCount = arr2.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "…" : s;
}

/// <summary>
/// 操作详情（行选中联动）
/// </summary>
internal class ReviewActionDetailSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ReviewActionDetailSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var actionIdStr = query?.Extra?["_id"]?.ToString();
        if (!int.TryParse(actionIdStr, out var actionId) || actionId <= 0)
        {
            return new DataResult
            {
                Data = new JsonObject
                {
                    ["action"] = "—",
                    ["time"] = "点击左侧操作查看详情",
                    ["summary"] = "—",
                    ["detail"] = "—"
                }
            };
        }

        var route = query?.RouteParams?.GetValueOrDefault("id");

        // 活跃会话：从上下文提取
        if (route == null || route == "0")
        {
            var review = _engine.GetActiveEnginesSnapshot().OfType<ReviewEngine>().FirstOrDefault();
            var ctx = review?.GetContextSnapshot();
            if (ctx == null)
                return new DataResult { Data = NotFoundObj() };

            int seq = 0;
            foreach (var msg in ctx.Messages)
            {
                if (msg.Parts == null) continue;
                foreach (var p in msg.Parts)
                {
                    if (p.ToolName != null) seq++;
                    if (seq == actionId)
                    {
                        return new DataResult
                        {
                            Data = new JsonObject
                            {
                                ["action"] = p.ToolName,
                                ["time"] = "—",
                                ["summary"] = p.ToolInput ?? "",
                                ["detail"] = "(活跃会话，详细返回值需等会话结束)"
                            }
                        };
                    }
                }
            }
            return new DataResult { Data = NotFoundObj() };
        }

        // 历史会话：从 DB
        if (!int.TryParse(route, out var sessionId))
            return new DataResult { Data = NotFoundObj() };

        var actions = await _engine.ReviewLogs.GetActionsBySessionAsync(sessionId);
        var action = actions.FirstOrDefault(a => a.Id == actionId);
        if (action == null)
            return new DataResult { Data = NotFoundObj() };

        return new DataResult
        {
            Data = new JsonObject
            {
                ["action"] = action.ActionType,
                ["time"] = action.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                ["summary"] = action.Summary,
                ["detail"] = action.Detail ?? "（无详情）"
            }
        };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });

    private static JsonObject NotFoundObj() => new()
    {
        ["action"] = "未找到",
        ["time"] = "—",
        ["summary"] = "—",
        ["detail"] = "—"
    };
}

/// <summary>
/// Review 配置读写
/// </summary>
internal class ReviewConfigSource : IDataSource
{
    private static string ConfigPath => Path.Combine(PathConfig.StoragePath, "Dream", "ReviewConfig.json");
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var cfg = ReviewConfig.Load(ConfigPath);
        var data = new JsonObject
        {
            ["Enabled"] = cfg.Enabled,
            ["TokenBudget"] = cfg.TokenBudget,
            ["ReserveBudget"] = cfg.ReserveBudget,
            ["CompressionThreshold"] = cfg.CompressionThreshold,
            ["MaxNavigationRounds"] = cfg.MaxNavigationRounds,
            ["EvaluationRate"] = cfg.EvaluationRate,
            ["PersonFloor"] = cfg.PersonFloor,
            ["PersonCeiling"] = cfg.PersonCeiling,
            ["PersonBaseline"] = cfg.PersonBaseline,
            ["StrangerMinMessages"] = cfg.StrangerMinMessages,
            ["UnderstandingMinMemories"] = cfg.UnderstandingMinMemories,
            ["UnderstandingMinDays"] = cfg.UnderstandingMinDays,
            ["UnderstandingAnyDimension"] = cfg.UnderstandingAnyDimension,
            ["FamiliarityMinDays"] = cfg.FamiliarityMinDays,
            ["FamiliarityMajorityDimension"] = cfg.FamiliarityMajorityDimension,
            ["TrustMinDays"] = cfg.TrustMinDays,
            ["TrustMinReviewCount"] = cfg.TrustMinReviewCount,
            ["TrustAllDimensions"] = cfg.TrustAllDimensions,
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action != "save" || data is not JsonObject payload)
            return Task.FromResult(new ActionResult { Success = false, Message = "无效请求" });

        try
        {
            var cfg = ReviewConfig.Load(ConfigPath);

            foreach (var (key, value) in payload)
            {
                var v = value?.ToString() ?? "";
                switch (key)
                {
                    case "Enabled": if (bool.TryParse(v, out var en)) cfg.Enabled = en; break;
                    case "TokenBudget": if (int.TryParse(v, out var tb)) cfg.TokenBudget = tb; break;
                    case "ReserveBudget": if (int.TryParse(v, out var rb)) cfg.ReserveBudget = rb; break;
                    case "CompressionThreshold": if (int.TryParse(v, out var ct2)) cfg.CompressionThreshold = ct2; break;
                    case "MaxNavigationRounds": if (int.TryParse(v, out var mn)) cfg.MaxNavigationRounds = mn; break;
                    case "EvaluationRate": if (float.TryParse(v, out var er)) cfg.EvaluationRate = er; break;
                    case "PersonFloor": if (float.TryParse(v, out var pf)) cfg.PersonFloor = pf; break;
                    case "PersonCeiling": if (float.TryParse(v, out var pc)) cfg.PersonCeiling = pc; break;
                    case "PersonBaseline": if (float.TryParse(v, out var pb)) cfg.PersonBaseline = pb; break;
                    case "StrangerMinMessages": if (int.TryParse(v, out var sm)) cfg.StrangerMinMessages = sm; break;
                    case "UnderstandingMinMemories": if (int.TryParse(v, out var um)) cfg.UnderstandingMinMemories = um; break;
                    case "UnderstandingMinDays": if (int.TryParse(v, out var ud)) cfg.UnderstandingMinDays = ud; break;
                    case "UnderstandingAnyDimension": if (float.TryParse(v, out var ua)) cfg.UnderstandingAnyDimension = ua; break;
                    case "FamiliarityMinDays": if (int.TryParse(v, out var fd)) cfg.FamiliarityMinDays = fd; break;
                    case "FamiliarityMajorityDimension": if (float.TryParse(v, out var fm)) cfg.FamiliarityMajorityDimension = fm; break;
                    case "TrustMinDays": if (int.TryParse(v, out var td)) cfg.TrustMinDays = td; break;
                    case "TrustMinReviewCount": if (int.TryParse(v, out var tr)) cfg.TrustMinReviewCount = tr; break;
                    case "TrustAllDimensions": if (float.TryParse(v, out var ta)) cfg.TrustAllDimensions = ta; break;
                }
            }

            cfg.Save(ConfigPath);
            return Task.FromResult(new ActionResult { Success = true, Message = "Review 配置已保存" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ActionResult { Success = false, Message = $"保存失败: {ex.Message}" });
        }
    }
}

/// <summary>
/// 会话的原始评价记录（优先从 RawEvaluations 快照读取，回退到 ReviewActions 解析）
/// </summary>
internal class ReviewRawEvaluationsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public ReviewRawEvaluationsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var route = query?.RouteParams?.GetValueOrDefault("id");
        var isActive = route == null || route == "0";

        if (isActive)
        {
            var review = _engine.GetActiveEnginesSnapshot().OfType<ReviewEngine>().FirstOrDefault();
            if (review == null)
                return new DataResult { Data = new JsonArray(), TotalCount = 0 };

            var buffer = review.EvaluationBuffer;
            var arr = new JsonArray();
            foreach (var e in buffer)
            {
                arr.Add(new JsonObject
                {
                    ["target"] = $"{e.TargetType}#{e.TargetId}",
                    ["dimension"] = e.Dimension,
                    ["rating"] = e.Rating
                });
            }
            return new DataResult { Data = arr, TotalCount = arr.Count };
        }

        if (!int.TryParse(route, out var sessionId))
            return new DataResult { Data = new JsonArray(), TotalCount = 0 };

        var sessions = await _engine.ReviewLogs.GetRecentSessionsAsync(500);
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null)
            return new DataResult { Data = new JsonArray(), TotalCount = 0 };

        var arr2 = new JsonArray();

        // 优先从 RawEvaluations 快照读取
        if (!string.IsNullOrEmpty(session.RawEvaluations))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<JsonElement>>(session.RawEvaluations);
                if (parsed != null)
                {
                    foreach (var item in parsed)
                    {
                        arr2.Add(new JsonObject
                        {
                            ["target"] = item.TryGetProperty("TargetType", out var tt) && item.TryGetProperty("TargetId", out var ti)
                                ? $"{tt.GetString()}#{ti.GetInt32()}" : "?",
                            ["dimension"] = item.TryGetProperty("Dimension", out var d) ? d.GetString() ?? "" : "",
                            ["rating"] = item.TryGetProperty("Rating", out var r) ? r.GetString() ?? "" : ""
                        });
                    }
                    return new DataResult { Data = arr2, TotalCount = arr2.Count };
                }
            }
            catch { }
        }

        return new DataResult { Data = arr2, TotalCount = arr2.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}
