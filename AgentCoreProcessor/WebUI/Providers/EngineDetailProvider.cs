using System;
using System.Collections.Generic;
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

            // Channel 引擎监视页面已移除：express 会话化后频道响应由 express/working 会话承载，
            // 详情统一在 /p/sessions 查看（SessionEngine 快照），不再有 per-channel ExpressEngine。

            // Vision
            var visionCheck = _engine.GetSpawnCheck<VisionEngineSpawnCheck>();
            if (visionCheck?.ActiveInstance != null)
                pages.Add(BuildVisionPage(visionCheck));

            return pages;
        }
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
}
// ---- 数据源 ----

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

