using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class DelegationProvider : IWebUIProvider
{
    public string Id => "core-delegation";
    public string DisplayName => "委托系统";

    private readonly MasterEngine _engine;

    public DelegationProvider(MasterEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<PageDefinition> Pages => new List<PageDefinition>
    {
        BuildOverviewPage(),
        BuildDetailPage(),
        BuildHistoryPage()
    };

    // ======== 总览 /p/delegation ========

    private PageDefinition BuildOverviewPage() => new()
    {
        Route = "delegation",
        Meta = new PageMeta { Title = "委托总览", Icon = "bi-diagram-3", Group = "系统引擎", Order = 33 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "delegation-stats", Type = CardType.Status, DataSourceId = "delegation-stats", Title = "委托统计",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "active", Label = "活跃中", Type = StatusFieldType.Badge },
                        new() { Field = "completed", Label = "已完成/失败" },
                        new() { Field = "stale", Label = "超时/空闲/归档" },
                        new() { Field = "loops", Label = "活跃循环" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 3 }
            },
            new()
            {
                Id = "delegation-active", Type = CardType.Table, DataSourceId = "delegation-active", Title = "活跃请求",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "time", Header = "时间", Width = "120px" },
                        new() { Field = "initiator", Header = "发起者", Width = "100px" },
                        new() { Field = "target", Header = "目标", Width = "100px" },
                        new() { Field = "title", Header = "标题" },
                        new() { Field = "state", Header = "状态", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "responses_count", Header = "回应", Width = "60px" }
                    },
                    Filters = new()
                    {
                        new() { Field = "state", Label = "状态", Options = new()
                        {
                            new() { Value = "", Label = "全部" },
                            new() { Value = "Submitted", Label = "待处理" },
                            new() { Value = "Accepted", Label = "已接受" },
                            new() { Value = "InProgress", Label = "执行中" },
                            new() { Value = "Rejected", Label = "已拒绝" }
                        }}
                    },
                    DefaultPageSize = 20
                },
                Layout = new CardLayout { PreferredCols = 12 }
            },
            new()
            {
                Id = "delegation-loops", Type = CardType.Table, DataSourceId = "delegation-loops", Title = "活跃循环",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "loop_id", Header = "循环标识" },
                        new() { Field = "type", Header = "类型", Width = "100px", Format = ColumnFormat.Badge }
                    },
                    DefaultPageSize = 20
                },
                Layout = new CardLayout { PreferredCols = 6 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "delegation-stats", Source = new DelegationStatsSource(_engine) },
            new() { Id = "delegation-active", Source = new DelegationActiveSource(_engine) },
            new() { Id = "delegation-loops", Source = new DelegationLoopsSource(_engine) }
        }
    };

    // ======== 请求详情 /p/delegation/request/{id} ========

    private PageDefinition BuildDetailPage() => new()
    {
        Route = "delegation/request/{id}",
        Meta = new PageMeta { Title = "请求详情", Icon = "bi-chat-right-text", Group = "系统引擎", ShowInNav = false },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "delegation-detail", Type = CardType.Status, DataSourceId = "delegation-detail", Title = "请求信息",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "request_id", Label = "请求ID" },
                        new() { Field = "initiator", Label = "发起者" },
                        new() { Field = "target", Label = "目标" },
                        new() { Field = "state", Label = "状态", Type = StatusFieldType.Badge },
                        new() { Field = "title", Label = "标题" },
                        new() { Field = "content", Label = "内容", IsMultiline = true },
                        new() { Field = "timeline", Label = "时间线" }
                    },
                    Actions = new()
                    {
                        new() { Id = "archive", Label = "归档", Confirm = "确认归档此请求？归档后所有接受者将收到通知。" },
                        new() { Id = "idle", Label = "设为空闲" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            },
            new()
            {
                Id = "delegation-responses", Type = CardType.Table, DataSourceId = "delegation-responses", Title = "回应时间线",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "seq", Header = "#", Width = "40px" },
                        new() { Field = "responder", Header = "回应者", Width = "110px" },
                        new() { Field = "type", Header = "类型", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "content", Header = "内容" },
                        new() { Field = "time", Header = "时间", Width = "140px" }
                    },
                    DefaultPageSize = 50
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "delegation-detail", Source = new DelegationDetailSource(_engine) },
            new() { Id = "delegation-responses", Source = new DelegationResponsesSource(_engine) }
        }
    };

    // ======== 历史 /p/delegation/history ========

    private PageDefinition BuildHistoryPage() => new()
    {
        Route = "delegation/history",
        Meta = new PageMeta { Title = "历史记录", Icon = "bi-clock-history", Group = "系统引擎", Order = 34 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "delegation-history", Type = CardType.Table, DataSourceId = "delegation-history", Title = "历史请求",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "time", Header = "提交", Width = "120px" },
                        new() { Field = "initiator", Header = "发起者", Width = "100px" },
                        new() { Field = "target", Header = "目标", Width = "100px" },
                        new() { Field = "title", Header = "标题" },
                        new() { Field = "state", Header = "状态", Width = "90px", Format = ColumnFormat.Badge },
                        new() { Field = "responses_count", Header = "回应", Width = "60px" },
                        new() { Field = "completed_time", Header = "完成", Width = "120px" }
                    },
                    Filters = new()
                    {
                        new() { Field = "state", Label = "状态", Options = new()
                        {
                            new() { Value = "", Label = "全部" },
                            new() { Value = "Completed", Label = "已完成" },
                            new() { Value = "Failed", Label = "失败" },
                            new() { Value = "Timeout", Label = "超时" },
                            new() { Value = "Idle", Label = "空闲" },
                            new() { Value = "Archived", Label = "已归档" }
                        }}
                    },
                    DefaultPageSize = 30
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "delegation-history", Source = new DelegationHistorySource(_engine) }
        }
    };
}

// ======== 数据源 ========

/// <summary>
/// 委托统计概览
/// </summary>
internal class DelegationStatsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DelegationStatsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = _engine.CrossRequests.GetAll();
        var activeLoops = _engine.DelegationBus.GetActiveLoopIds();

        var active = all.Count(r => r.State is CrossRequestState.Submitted
            or CrossRequestState.Accepted or CrossRequestState.InProgress);
        var completed = all.Count(r => r.State is CrossRequestState.Completed or CrossRequestState.Failed);
        var stale = all.Count(r => r.State is CrossRequestState.Timeout
            or CrossRequestState.Idle or CrossRequestState.Archived);

        return Task.FromResult(new DataResult
        {
            Data = new JsonObject
            {
                ["active"] = $"{active} 个",
                ["completed"] = $"{completed} 个",
                ["stale"] = $"{stale} 个",
                ["loops"] = $"{activeLoops.Count} 个 ({string.Join(", ", activeLoops)})"
            }
        });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

/// <summary>
/// 活跃请求列表（Submitted/Accepted/InProgress/Rejected）
/// </summary>
internal class DelegationActiveSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DelegationActiveSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = _engine.CrossRequests.GetAll()
            .Where(r => r.State is CrossRequestState.Submitted
                or CrossRequestState.Accepted or CrossRequestState.InProgress
                or CrossRequestState.Rejected)
            .OrderByDescending(r => r.SubmittedAt)
            .AsEnumerable();

        // 状态过滤
        var stateFilter = query?.Filters?.FirstOrDefault(f => f.Field == "state")?.Value;
        if (!string.IsNullOrEmpty(stateFilter))
        {
            all = all.Where(r => r.State.ToString() == stateFilter);
        }

        var allList = all.ToList();
        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 20;
        var paged = allList.Skip((page - 1) * pageSize).Take(pageSize);

        var arr = new JsonArray();
        foreach (var r in paged)
        {
            arr.Add(new JsonObject
            {
                ["time"] = r.SubmittedAt.ToString("MM-dd HH:mm:ss"),
                ["initiator"] = r.InitiatorId,
                ["target"] = r.TargetId ?? "广播",
                ["title"] = r.Title.Length > 80 ? r.Title[..80] + "…" : r.Title,
                ["state"] = r.State.ToString(),
                ["responses_count"] = r.Responses.Count.ToString(),
                ["_link"] = $"/p/delegation/request/{r.RequestId}"
            });
        }

        return Task.FromResult(new DataResult { Data = arr, TotalCount = allList.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

/// <summary>
/// 活跃循环列表
/// </summary>
internal class DelegationLoopsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DelegationLoopsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var loopIds = _engine.DelegationBus.GetActiveLoopIds();
        var arr = new JsonArray();
        foreach (var id in loopIds)
        {
            var type = id switch
            {
                string s when s == LoopId.System => "系统",
                string s when LoopId.IsChannel(s, out _) => "频道",
                string s when s.StartsWith("task:") => "子任务",
                string s when s.StartsWith("review:") => "复盘",
                _ => "未知"
            };
            arr.Add(new JsonObject
            {
                ["loop_id"] = id,
                ["type"] = type
            });
        }

        if (arr.Count == 0)
        {
            arr.Add(new JsonObject
            {
                ["loop_id"] = "（无活跃循环）",
                ["type"] = "—"
            });
        }

        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

/// <summary>
/// 请求详情卡片
/// </summary>
internal class DelegationDetailSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DelegationDetailSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var requestId = query?.RouteParams?.GetValueOrDefault("id");
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return Task.FromResult(new DataResult
            {
                Data = new JsonObject
                {
                    ["request_id"] = "—",
                    ["initiator"] = "—",
                    ["target"] = "—",
                    ["state"] = "未找到",
                    ["title"] = "未指定请求ID",
                    ["content"] = "—",
                    ["timeline"] = "—"
                }
            });
        }

        var r = _engine.CrossRequests.Get(requestId);
        if (r == null)
        {
            return Task.FromResult(new DataResult
            {
                Data = new JsonObject
                {
                    ["request_id"] = requestId,
                    ["initiator"] = "—",
                    ["target"] = "—",
                    ["state"] = "不存在",
                    ["title"] = "请求未找到",
                    ["content"] = $"请求 {requestId} 可能已被清理或从未创建。",
                    ["timeline"] = "—"
                }
            });
        }

        var timeline = $"提交: {r.SubmittedAt:MM-dd HH:mm:ss}";
        if (r.ExpiresAt > DateTime.MinValue)
            timeline += $" | 超时: {r.ExpiresAt:MM-dd HH:mm:ss}";
        if (r.CompletedAt != null)
            timeline += $" | 完成: {r.CompletedAt:MM-dd HH:mm:ss}";

        var data = new JsonObject
        {
            ["request_id"] = r.RequestId,
            ["initiator"] = r.InitiatorId,
            ["target"] = r.TargetId ?? "广播",
            ["state"] = r.State.ToString(),
            ["title"] = r.Title,
            ["content"] = r.Content,
            ["timeline"] = timeline
        };

        var disabledActions = new JsonArray();
        if (r.State is CrossRequestState.Archived)
        {
            disabledActions.Add("archive");
            disabledActions.Add("idle");
        }
        else if (r.State is CrossRequestState.Timeout or CrossRequestState.Idle
            or CrossRequestState.Completed or CrossRequestState.Failed)
        {
            disabledActions.Add("idle");
        }
        if (disabledActions.Count > 0)
            data["_disabled_actions"] = disabledActions;

        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var requestId = data?["requestId"]?.ToString();
        if (string.IsNullOrWhiteSpace(requestId))
            return Task.FromResult(new ActionResult { Success = false, Message = "缺少 requestId" });

        switch (action)
        {
            case "archive":
                _engine.CrossRequests.Archive(requestId);
                return Task.FromResult(new ActionResult { Success = true, Message = $"请求 {requestId} 已归档" });
            case "idle":
                _engine.CrossRequests.Idle(requestId);
                return Task.FromResult(new ActionResult { Success = true, Message = $"请求 {requestId} 已设为空闲" });
            default:
                return Task.FromResult(new ActionResult { Success = false, Message = $"未知操作: {action}" });
        }
    }
}

/// <summary>
/// 回应时间线表格
/// </summary>
internal class DelegationResponsesSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DelegationResponsesSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var requestId = query?.RouteParams?.GetValueOrDefault("id");
        if (string.IsNullOrWhiteSpace(requestId))
            return Task.FromResult(new DataResult { Data = new JsonArray(), TotalCount = 0 });

        var r = _engine.CrossRequests.Get(requestId);
        if (r == null)
            return Task.FromResult(new DataResult { Data = new JsonArray(), TotalCount = 0 });

        var arr = new JsonArray();
        foreach (var resp in r.Responses.OrderBy(x => x.SequenceNumber))
        {
            var content = resp.Content;
            if (content.Length > 200) content = content[..200] + "…";

            arr.Add(new JsonObject
            {
                ["seq"] = resp.SequenceNumber.ToString(),
                ["responder"] = resp.ResponderId,
                ["type"] = resp.Type.ToString(),
                ["content"] = content,
                ["time"] = resp.Timestamp.ToString("MM-dd HH:mm:ss")
            });
        }

        if (arr.Count == 0)
        {
            arr.Add(new JsonObject
            {
                ["seq"] = "—",
                ["responder"] = "—",
                ["type"] = "—",
                ["content"] = "暂无回应",
                ["time"] = "—"
            });
        }

        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

/// <summary>
/// 历史请求列表（Completed/Failed/Timeout/Idle/Archived）
/// </summary>
internal class DelegationHistorySource : IDataSource
{
    private readonly MasterEngine _engine;
    public DelegationHistorySource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var all = _engine.CrossRequests.GetAll()
            .Where(r => r.State is CrossRequestState.Completed
                or CrossRequestState.Failed or CrossRequestState.Timeout
                or CrossRequestState.Idle or CrossRequestState.Archived
                or CrossRequestState.Rejected)
            .OrderByDescending(r => r.CompletedAt ?? r.SubmittedAt)
            .AsEnumerable();

        // 状态过滤
        var stateFilter = query?.Filters?.FirstOrDefault(f => f.Field == "state")?.Value;
        if (!string.IsNullOrEmpty(stateFilter))
        {
            all = all.Where(r => r.State.ToString() == stateFilter);
        }

        var allList = all.ToList();
        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 30;
        var paged = allList.Skip((page - 1) * pageSize).Take(pageSize);

        var arr = new JsonArray();
        foreach (var r in paged)
        {
            arr.Add(new JsonObject
            {
                ["time"] = r.SubmittedAt.ToString("MM-dd HH:mm:ss"),
                ["initiator"] = r.InitiatorId,
                ["target"] = r.TargetId ?? "广播",
                ["title"] = r.Title.Length > 80 ? r.Title[..80] + "…" : r.Title,
                ["state"] = r.State.ToString(),
                ["responses_count"] = r.Responses.Count.ToString(),
                ["completed_time"] = r.CompletedAt?.ToString("MM-dd HH:mm:ss") ?? "—",
                ["_link"] = $"/p/delegation/request/{r.RequestId}"
            });
        }

        return Task.FromResult(new DataResult { Data = arr, TotalCount = allList.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}
