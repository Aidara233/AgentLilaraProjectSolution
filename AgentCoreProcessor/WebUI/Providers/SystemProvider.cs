using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.WebUI.Services;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class SystemProvider : IWebUIProvider
{
    public string Id => "core-system";
    public string DisplayName => "系统循环";

    private readonly MasterEngine _engine;

    public SystemProvider(MasterEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<PageDefinition> Pages => new List<PageDefinition>
    {
        BuildOverviewPage(),
        BuildEventsPage(),
        BuildAgentsPage()
    };

    // ---- 概览页 ----

    private PageDefinition BuildOverviewPage() => new()
    {
        Route = "system",
        Meta = new PageMeta { Title = "概览", Icon = "bi-gear", Group = "系统循环", Order = 20 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "sys-status", Type = CardType.Status, DataSourceId = "sys-status", Title = "引擎状态",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "state", Label = "状态", Type = StatusFieldType.Indicator },
                        new() { Field = "rounds", Label = "轮次" },
                        new() { Field = "tokens", Label = "Token 用量" },
                        new() { Field = "compression", Label = "压缩层级", Type = StatusFieldType.Badge },
                        new() { Field = "backoff", Label = "退避", Type = StatusFieldType.Badge },
                        new() { Field = "queue", Label = "任务队列" },
                        new() { Field = "agents", Label = "子agent" },
                        new() { Field = "errors", Label = "错误" }
                    },
                    Actions = new()
                    {
                        new() { Id = "force-wake", Label = "强制唤醒", Confirm = "确认强制唤醒系统循环？" },
                        new() { Id = "force-compress", Label = "强制压缩", Confirm = "确认强制压缩上下文？" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            },
            new()
            {
                Id = "sys-context", Type = CardType.Stream, DataSourceId = "sys-context", Title = "上下文",
                Schema = new StreamSchema
                {
                    MaxLines = 500,
                    AutoScroll = false,
                    ShowPauseButton = false,
                    ShowFilter = true
                },
                Layout = new CardLayout { PreferredCols = 12, Height = "600px" }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "sys-status", Source = new SystemOverviewStatusSource(_engine) },
            new() { Id = "sys-context", Source = new SystemOverviewContextSource(_engine) }
        }
    };

    // ---- 事件队列页 ----

    private PageDefinition BuildEventsPage() => new()
    {
        Route = "system/events",
        Meta = new PageMeta { Title = "事件队列", Icon = "bi-inbox", Group = "系统循环", Order = 21 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "sys-events", Type = CardType.Table, DataSourceId = "sys-events", Title = "事件队列",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "time", Header = "时间", Width = "140px" },
                        new() { Field = "type", Header = "类型", Width = "100px", Format = ColumnFormat.Badge },
                        new() { Field = "source", Header = "来源", Width = "120px" },
                        new() { Field = "description", Header = "描述" },
                        new() { Field = "status", Header = "状态", Width = "80px", Format = ColumnFormat.Badge }
                    },
                    DefaultPageSize = 30
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "sys-events", Source = new SystemEventsSource(_engine) }
        }
    };

    private PageDefinition BuildAgentsPage() => new()
    {
        Route = "system/agents",
        Meta = new PageMeta { Title = "子agent", Icon = "bi-people", Group = "系统循环", Order = 22 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "sys-agents", Type = CardType.Table, DataSourceId = "sys-agents", Title = "子agent",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "session", Header = "会话ID", Width = "140px" },
                        new() { Field = "status", Header = "状态", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "instruction", Header = "指令" },
                        new() { Field = "delegation", Header = "委派", Width = "100px" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "sys-agents", Source = new SystemAgentsSource(_engine) }
        }
    };
}

// ---- 数据源 ----

internal class SystemOverviewStatusSource : IDataSource
{
    private readonly MasterEngine _engine;
    public SystemOverviewStatusSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<SystemEngineSpawnCheck>();
        var snap = check?.GetSystemSnapshot() ?? new SystemEngineSnapshot();
        var ctx = check?.GetContextSnapshot();

        var data = new JsonObject
        {
            ["state"] = snap.IsAlive ? "运行中" : "停止",
            ["rounds"] = ctx?.TotalRounds.ToString() ?? "—",
            ["tokens"] = ctx != null ? $"{ctx.EstimatedTokens / 1000}k ({ctx.MessageCount} 条)" : "—",
            ["compression"] = ctx?.CompressionTier.ToString() ?? "None",
            ["backoff"] = ctx?.IsInBackoff == true ? "退避中" : "正常",
            ["queue"] = $"{snap.TaskQueueDepth} 待处理",
            ["agents"] = $"{snap.ActiveSubAgentCount} 活跃 / {snap.SubAgents.Count} 总计",
            ["errors"] = snap.ConsecutiveFailures > 0
                ? $"连续失败 {snap.ConsecutiveFailures} 次 (共 {snap.TotalErrorCount})"
                : snap.TotalErrorCount > 0 ? $"共 {snap.TotalErrorCount} 次" : "无"
        };

        if (!snap.IsAlive)
            data["_disabled_actions"] = new JsonArray("force-wake", "force-compress");

        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<SystemEngineSpawnCheck>();
        if (check == null)
            return Task.FromResult(new ActionResult { Success = false, Message = "系统循环未注册" });

        switch (action)
        {
            case "force-wake":
                check.ForceWake();
                return Task.FromResult(new ActionResult { Success = true, Message = "已发送唤醒信号" });
            case "force-compress":
                check.ForceCompress();
                return Task.FromResult(new ActionResult { Success = true, Message = "已触发压缩" });
            default:
                return Task.FromResult(new ActionResult { Success = false, Message = $"未知操作: {action}" });
        }
    }
}

internal class SystemOverviewContextSource : IDataSource
{
    private readonly MasterEngine _engine;
    public SystemOverviewContextSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 10000, 10000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<SystemEngineSpawnCheck>();
        var snap = check?.GetContextSnapshot();
        if (snap == null)
            return Task.FromResult(new DataResult { Data = new JsonArray("系统循环未激活") });

        var arr = new JsonArray();
        if (snap.Summary != null)
            arr.Add($"[摘要] {snap.Summary}");

        arr.Add($"--- 共 {snap.MessageCount} 条 | {snap.EstimatedTokens}t | 对话起始: #{snap.ConversationOffset} ---");

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
                        arr.Add($"{prefix} {role}{tokens} tool_use:{p.ToolName} → {Truncate(p.ToolInput, 150)}");
                    else if (p.IsError == true)
                        arr.Add($"{prefix} {role}{tokens} [ERROR] {Truncate(p.Text, 200)}");
                    else if (p.Text != null)
                        arr.Add($"{prefix} {role}{tokens} {Truncate(p.Text, 300)}");
                }
            }
            else
            {
                arr.Add($"{prefix} {role}{tokens} {Truncate(msg.Content, 300)}");
            }
            index++;
        }
        return Task.FromResult(new DataResult { Data = arr });
    }

    private static string Truncate(string? s, int max)
        => s == null ? "" : s.Length <= max ? s : s[..max] + "...";

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class SystemEventsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public SystemEventsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var arr = new JsonArray();

        // 待评估委派
        var pendingDelegations = _engine.Delegations.GetPendingForEvaluation();
        foreach (var d in pendingDelegations)
        {
            arr.Add(new JsonObject
            {
                ["time"] = d.SubmittedAt.ToString("MM-dd HH:mm:ss"),
                ["type"] = "委派",
                ["source"] = $"频道#{d.SourceChannelId}",
                ["description"] = d.Description.Length > 100 ? d.Description[..100] + "..." : d.Description,
                ["status"] = "待评估"
            });
        }

        // 执行中委派
        var acceptedDelegations = _engine.Delegations.GetAcceptedForExecution();
        foreach (var d in acceptedDelegations)
        {
            arr.Add(new JsonObject
            {
                ["time"] = (d.EvaluatedAt ?? d.SubmittedAt).ToString("MM-dd HH:mm:ss"),
                ["type"] = "委派",
                ["source"] = $"频道#{d.SourceChannelId}",
                ["description"] = d.Description.Length > 100 ? d.Description[..100] + "..." : d.Description,
                ["status"] = "待执行"
            });
        }

        // 重试中委派
        var retryDelegations = _engine.Delegations.GetRetryPending();
        foreach (var d in retryDelegations)
        {
            arr.Add(new JsonObject
            {
                ["time"] = (d.CompletedAt ?? d.SubmittedAt).ToString("MM-dd HH:mm:ss"),
                ["type"] = "委派",
                ["source"] = $"频道#{d.SourceChannelId}",
                ["description"] = $"[重试{d.RetryCount}/{DelegationRegistry.MaxRetries}] {(d.Description.Length > 80 ? d.Description[..80] + "..." : d.Description)}",
                ["status"] = "重试中"
            });
        }

        // 任务队列深度（无法逐条读取 Channel<T>，只显示计数）
        var taskCount = _engine.TaskBridge.PendingTaskCount;
        if (taskCount > 0)
        {
            arr.Add(new JsonObject
            {
                ["time"] = DateTime.Now.ToString("MM-dd HH:mm:ss"),
                ["type"] = "任务",
                ["source"] = "TaskBridge",
                ["description"] = $"{taskCount} 个任务等待系统循环处理",
                ["status"] = "排队中"
            });
        }

        if (arr.Count == 0)
        {
            arr.Add(new JsonObject
            {
                ["time"] = DateTime.Now.ToString("MM-dd HH:mm:ss"),
                ["type"] = "—",
                ["source"] = "—",
                ["description"] = "当前无待处理事件",
                ["status"] = "空闲"
            });
        }

        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class SystemAgentsSource : IDataSource
{
    private readonly MasterEngine _engine;
    public SystemAgentsSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<SystemEngineSpawnCheck>();
        var snap = check?.GetSystemSnapshot();
        if (snap == null)
            return Task.FromResult(new DataResult { Data = new JsonArray(), TotalCount = 0 });

        var arr = new JsonArray();
        foreach (var agent in snap.SubAgents)
        {
            var instruction = agent.CurrentInstruction ?? agent.LastResult ?? "—";
            if (instruction.Length > 120) instruction = instruction[..120] + "...";

            arr.Add(new JsonObject
            {
                ["session"] = agent.SessionId,
                ["status"] = agent.IsAlive ? "运行中" : "已完成",
                ["instruction"] = instruction,
                ["delegation"] = agent.DelegationId ?? "—"
            });
        }

        return Task.FromResult(new DataResult { Data = arr, TotalCount = arr.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}
