using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Engine.Vision;
using AgentCoreProcessor.WebUI.Services;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class EngineDetailProvider : IWebUIProvider
{
    public string Id => "core-engine-detail";
    public string DisplayName => "引擎详情";

    private readonly MasterEngine _engine;

    public EngineDetailProvider(MasterEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<PageDefinition> Pages
    {
        get
        {
            var pages = new List<PageDefinition>();

            // System
            var systemCheck = _engine.GetSpawnCheck<SystemEngineSpawnCheck>();
            if (systemCheck?.GetSystemSnapshot()?.IsAlive == true)
                pages.Add(BuildSystemPage(systemCheck));

            // Channel (multi-instance) — 引擎监视页面
            var channelCheck = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
            if (channelCheck != null)
            {
                foreach (var (id, ch) in channelCheck.GetActiveChannels())
                {
                    if (!ch.IsAlive) continue;
                    pages.Add(BuildChannelEnginePage(ch, id));
                }
            }

            // Dream
            if (_engine.HasActiveEngine("Dream"))
            {
                var dreamCheck = _engine.GetSpawnCheck<DreamEngineSpawnCheck>();
                if (dreamCheck != null)
                    pages.Add(BuildDreamPage(dreamCheck));
            }

            // Vision
            var visionCheck = _engine.GetSpawnCheck<VisionEngineSpawnCheck>();
            if (visionCheck?.ActiveInstance != null)
                pages.Add(BuildVisionPage(visionCheck));

            // Review
            var allEngines = _engine.GetActiveEnginesSnapshot();
            var review = allEngines.OfType<ReviewEngine>().FirstOrDefault();
            if (review != null)
                pages.Add(BuildReviewPage(review));

            return pages;
        }
    }
    private PageDefinition BuildSystemPage(SystemEngineSpawnCheck check)
    {
        return new PageDefinition
        {
            Route = "engines/system",
            Meta = new PageMeta { Title = "系统循环", Icon = "bi-gear", Group = "", Order = -89, ShowInNav = false },
            Cards = new List<CardDefinition>
            {
                StatusCard("engine-status", "引擎状态", "engine-detail-status"),
                ContextCard("engine-context", "上下文", "engine-detail-context"),
                ActionCard("engine-actions", "操作", "engine-detail-actions")
            },
            DataSources = new List<DataSourceDefinition>
            {
                new() { Id = "engine-detail-status", Source = new SystemDetailStatusSource(check) },
                new() { Id = "engine-detail-context", Source = new EngineContextSource(() => check.GetContextSnapshot()) },
                new() { Id = "engine-detail-actions", Source = new EngineActionSource(
                    () => check.ForceWake(), () => check.ForceCompress()) }
            }
        };
    }

    private PageDefinition BuildChannelEnginePage(ChannelEngine ch, int channelId)
    {
        var snap = ch.GetSnapshot();
        var name = snap.ChannelName ?? $"#{channelId}";
        return new PageDefinition
        {
            Route = $"engines/channel_{channelId}",
            Meta = new PageMeta { Title = $"频道引擎: {name}", Icon = "bi-chat-dots", Group = "", Order = -88, ShowInNav = false },
            Cards = new List<CardDefinition>
            {
                StatusCard("engine-status", "引擎状态", "engine-detail-status"),
                ContextCard("engine-context", "上下文", "engine-detail-context"),
                ActionCard("engine-actions", "操作", "engine-detail-actions")
            },
            DataSources = new List<DataSourceDefinition>
            {
                new() { Id = "engine-detail-status", Source = new ChannelEngineStatusSource(ch) },
                new() { Id = "engine-detail-context", Source = new EngineContextSource(() => ch.GetContextSnapshot()) },
                new() { Id = "engine-detail-actions", Source = new EngineActionSource(
                    () => ch.ForceWake(), () => ch.ForceCompress()) }
            }
        };
    }

    private PageDefinition BuildDreamPage(DreamEngineSpawnCheck check)
    {
        return new PageDefinition
        {
            Route = "engines/dream",
            Meta = new PageMeta { Title = "做梦引擎", Icon = "bi-moon-stars", Group = "", Order = -87, ShowInNav = false },
            Cards = new List<CardDefinition>
            {
                StatusCard("engine-status", "引擎状态", "engine-detail-status")
            },
            DataSources = new List<DataSourceDefinition>
            {
                new() { Id = "engine-detail-status", Source = new DreamDetailStatusSource(check, _engine) }
            }
        };
    }

    private PageDefinition BuildVisionPage(VisionEngineSpawnCheck check)
    {
        return new PageDefinition
        {
            Route = "engines/vision",
            Meta = new PageMeta { Title = "视觉引擎", Icon = "bi-eye", Group = "", Order = -86, ShowInNav = false },
            Cards = new List<CardDefinition>
            {
                StatusCard("engine-status", "引擎状态", "engine-detail-status")
            },
            DataSources = new List<DataSourceDefinition>
            {
                new() { Id = "engine-detail-status", Source = new VisionDetailStatusSource(check) }
            }
        };
    }

    private PageDefinition BuildReviewPage(ReviewEngine review)
    {
        return new PageDefinition
        {
            Route = "engines/review",
            Meta = new PageMeta { Title = "复盘引擎", Icon = "bi-journal-check", Group = "", Order = -85, ShowInNav = false },
            Cards = new List<CardDefinition>
            {
                StatusCard("engine-status", "引擎状态", "engine-detail-status"),
                ContextCard("engine-context", "上下文", "engine-detail-context")
            },
            DataSources = new List<DataSourceDefinition>
            {
                new() { Id = "engine-detail-status", Source = new ReviewDetailStatusSource(review) },
                new() { Id = "engine-detail-context", Source = new EngineContextSource(() => review.GetContextSnapshot()) }
            }
        };
    }

    // ---- Card 模板 ----

    private static CardDefinition StatusCard(string id, string title, string dsId) => new()
    {
        Id = id, Type = CardType.Status, DataSourceId = dsId, Title = title,
        Schema = new StatusSchema
        {
            Fields = new()
            {
                new() { Field = "state", Label = "状态", Type = StatusFieldType.Indicator },
                new() { Field = "rounds", Label = "轮次" },
                new() { Field = "tokens", Label = "Token 用量" },
                new() { Field = "compression", Label = "压缩层级", Type = StatusFieldType.Badge },
                new() { Field = "backoff", Label = "退避", Type = StatusFieldType.Badge },
                new() { Field = "extra", Label = "附加信息" }
            }
        },
        Layout = new CardLayout { PreferredCols = 12 }
    };

    private static CardDefinition ContextCard(string id, string title, string dsId) => new()
    {
        Id = id, Type = CardType.Stream, DataSourceId = dsId, Title = title,
        Schema = new StreamSchema
        {
            MaxLines = 500,
            AutoScroll = false,
            ShowPauseButton = false,
            ShowFilter = true
        },
        Layout = new CardLayout { PreferredCols = 12, Height = "600px" }
    };

    private static CardDefinition ActionCard(string id, string title, string dsId) => new()
    {
        Id = id, Type = CardType.Status, DataSourceId = dsId, Title = title,
        Schema = new StatusSchema
        {
            Fields = new() { new() { Field = "info", Label = "可用操作" } },
            Actions = new()
            {
                new() { Id = "force-wake", Label = "强制唤醒", Confirm = "确认强制唤醒引擎？" },
                new() { Id = "force-compress", Label = "强制压缩", Confirm = "确认强制压缩上下文？" }
            }
        },
        Layout = new CardLayout { PreferredCols = 12 }
    };
}
// ---- 数据源 ----

internal class SystemDetailStatusSource : IDataSource
{
    private readonly SystemEngineSpawnCheck _check;
    public SystemDetailStatusSource(SystemEngineSpawnCheck check) => _check = check;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var snap = _check.GetSystemSnapshot();
        var ctx = _check.GetContextSnapshot();
        var data = new JsonObject
        {
            ["state"] = snap.IsAlive ? "运行中" : "停止",
            ["rounds"] = ctx?.TotalRounds.ToString() ?? "—",
            ["tokens"] = ctx != null ? $"{ctx.EstimatedTokens / 1000}k ({ctx.MessageCount} 条)" : "—",
            ["compression"] = ctx?.CompressionTier.ToString() ?? "None",
            ["backoff"] = ctx?.IsInBackoff == true ? "退避中" : "正常",
            ["extra"] = $"任务队列 {snap.TaskQueueDepth} | 子agent {snap.ActiveSubAgentCount} | 重启 {snap.RestartCount}次"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class ChannelEngineStatusSource : IDataSource
{
    private readonly ChannelEngine _ch;
    public ChannelEngineStatusSource(ChannelEngine ch) => _ch = ch;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var snap = _ch.GetSnapshot();
        var ctx = _ch.GetContextSnapshot();
        var mode = snap.IsWorkingMode ? "Working" : "Express";
        var data = new JsonObject
        {
            ["state"] = snap.IsBusy ? "处理中" : "等待",
            ["rounds"] = $"{snap.TotalRounds} (静默 {snap.SilentRounds})",
            ["tokens"] = ctx != null ? $"{ctx.EstimatedTokens / 1000}k ({ctx.MessageCount} 条)" : "—",
            ["compression"] = ctx?.CompressionTier.ToString() ?? "None",
            ["backoff"] = ctx?.IsInBackoff == true ? "退避中" : (snap.ConsecutiveFailures > 0 ? $"失败{snap.ConsecutiveFailures}次" : "正常"),
            ["extra"] = $"{mode} | 冲动 {snap.Impulse:F1}/{snap.Threshold:F1} | 工具 {snap.AuthorizedToolCount} | 参与者 {snap.ParticipantCount}"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class DreamDetailStatusSource : IDataSource
{
    private readonly DreamEngineSpawnCheck _check;
    private readonly MasterEngine _engine;
    public DreamDetailStatusSource(DreamEngineSpawnCheck check, MasterEngine engine)
    { _check = check; _engine = engine; }
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var snap = _check.GetDreamSnapshot(_engine.HasActiveEngine("Dream"));
        var data = new JsonObject
        {
            ["state"] = snap.HasActiveDream ? "做梦中" : "空闲",
            ["rounds"] = "—",
            ["tokens"] = "—",
            ["compression"] = "—",
            ["backoff"] = "—",
            ["extra"] = snap.HasActiveDream
                ? $"片段 {snap.FragmentsCompleted}/{snap.FragmentsTotal} | 当前: {snap.CurrentFragment ?? "—"}"
                : "—"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class VisionDetailStatusSource : IDataSource
{
    private readonly VisionEngineSpawnCheck _check;
    public VisionDetailStatusSource(VisionEngineSpawnCheck check) => _check = check;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var snap = _check.ActiveInstance?.GetSnapshot();
        var data = new JsonObject
        {
            ["state"] = snap?.IsBusy == true ? "处理中" : "空闲",
            ["rounds"] = "—",
            ["tokens"] = "—",
            ["compression"] = "—",
            ["backoff"] = snap?.VisionSuspended == true ? $"暂停: {snap.SuspendReason}" : "正常",
            ["extra"] = snap != null
                ? $"已处理 {snap.TotalProcessed} | 视觉错误 {snap.VisionErrors} | OCR错误 {snap.OcrErrors}"
                : "—"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class ReviewDetailStatusSource : IDataSource
{
    private readonly ReviewEngine _review;
    public ReviewDetailStatusSource(ReviewEngine review) => _review = review;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var ctx = _review.GetContextSnapshot();
        var data = new JsonObject
        {
            ["state"] = _review.IsAlive ? "运行中" : "停止",
            ["rounds"] = ctx?.TotalRounds.ToString() ?? "—",
            ["tokens"] = $"已用 {_review.TokensUsed / 1000}k | 上下文 {(ctx?.EstimatedTokens ?? 0) / 1000}k",
            ["compression"] = "规则压缩",
            ["backoff"] = ctx?.IsInBackoff == true ? "退避中" : "正常",
            ["extra"] = $"游标 ch{_review.CursorChannelId ?? 0}"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}
internal class EngineContextSource : IDataSource
{
    private readonly Func<EngineContextSnapshot?> _getSnapshot;
    public EngineContextSource(Func<EngineContextSnapshot?> getSnapshot) => _getSnapshot = getSnapshot;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 10000, 10000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var snap = _getSnapshot();
        if (snap == null)
            return Task.FromResult(new DataResult { Data = new JsonArray("Agent 未激活") });

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

internal class EngineActionSource : IDataSource
{
    private readonly Action _forceWake;
    private readonly Action _forceCompress;
    public EngineActionSource(Action forceWake, Action forceCompress)
    { _forceWake = forceWake; _forceCompress = forceCompress; }
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var data = new JsonObject { ["info"] = "强制唤醒 / 强制压缩" };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        switch (action)
        {
            case "force-wake":
                _forceWake();
                return Task.FromResult(new ActionResult { Success = true, Message = "已发送唤醒信号" });
            case "force-compress":
                _forceCompress();
                return Task.FromResult(new ActionResult { Success = true, Message = "已触发压缩" });
            default:
                return Task.FromResult(new ActionResult { Success = false, Message = $"未知操作: {action}" });
        }
    }
}



