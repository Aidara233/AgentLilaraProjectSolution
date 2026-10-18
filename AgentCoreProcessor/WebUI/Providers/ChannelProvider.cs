using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Util;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class ChannelProvider : IWebUIProvider
{
    public string Id => "core-channels";
    public string DisplayName => "频道";

    private readonly MasterEngine _engine;
    private List<Channel> _cachedChannels = new();

    public ChannelProvider(MasterEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<PageDefinition> Pages
    {
        get
        {
            var pages = new List<PageDefinition> { BuildListPage() };

            if (_cachedChannels.Count == 0 && _engine.Session != null)
            {
                try { _cachedChannels = _engine.Session.GetAllChannelsAsync().GetAwaiter().GetResult(); }
                catch { /* DB not ready yet */ }
            }

            foreach (var ch in _cachedChannels)
                pages.Add(BuildDetailPage(ch.Id, ch.Name));

            return pages;
        }
    }

    internal void RefreshCache(List<Channel> channels)
    {
        _cachedChannels = channels;
    }

    private PageDefinition BuildListPage() => new()
    {
        Route = "channels",
        Meta = new PageMeta { Title = "频道列表", Icon = "bi-chat-dots", Group = "频道引擎", Order = 40 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "channel-list",
                Type = CardType.Table,
                DataSourceId = "channel-list",
                Title = "频道列表",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "name", Header = "频道", Format = ColumnFormat.Link },
                        new() { Field = "engine", Header = "引擎状态", Width = "100px", Format = ColumnFormat.Badge },
                        new() { Field = "messages", Header = "消息数", Width = "100px" },
                        new() { Field = "affinity", Header = "亲和度", Width = "80px" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "channel-list", Source = new ChannelListSource(_engine, this), Paging = DataPagingMode.Client }
        }
    };

    private PageDefinition BuildDetailPage(int channelId, string channelName) => new()
    {
        Route = $"channels/{channelId}",
        Meta = new PageMeta { Title = $"频道: {channelName}", Icon = "bi-chat-dots", Group = "频道引擎", Order = 99, ShowInNav = false },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "ch-info", Type = CardType.Status, DataSourceId = "ch-info", Title = "频道信息",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "name", Label = "名称" },
                        new() { Field = "engine", Label = "引擎状态", Type = StatusFieldType.Badge },
                        new() { Field = "affinity", Label = "亲和度" },
                        new() { Field = "importance", Label = "重要性", Type = StatusFieldType.Badge },
                        new() { Field = "messages", Label = "消息总数" },
                        new() { Field = "extraction", Label = "记忆提取进度" },
                        new() { Field = "config", Label = "提取阈值" }
                    },
                    Actions = new()
                    {
                        new() { Id = "view-engine", Label = "查看引擎状态", Confirm = "" },
                        new() { Id = "force-extraction", Label = "强制提取记忆", Confirm = "确定要强制触发记忆提取吗？" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 },
                // 提取是 MindEngine 的全局攒批，不是频道私有队列；强制提取后会改变提取计数。
                AffectedDataSourceIds = new[] { "ch-info" }
            },
            new()
            {
                Id = "ch-messages", Type = CardType.Table, DataSourceId = "ch-messages", Title = "最近消息",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "time", Header = "时间", Width = "160px" },
                        new() { Field = "sender", Header = "发言人", Width = "120px" },
                        new() { Field = "content", Header = "内容" }
                    },
                    DefaultPageSize = 20
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "ch-info", Factory = () => new ChannelInfoSource(_engine, channelId) },
            new() { Id = "ch-messages", Factory = () => new ChannelMessagesSource(_engine, channelId), Paging = DataPagingMode.Server }
        }
    };
}

// ---- 数据源 ----

internal class ChannelListSource : IDataSource
{
    private readonly MasterEngine _engine;
    private readonly ChannelProvider _provider;
    public ChannelListSource(MasterEngine engine, ChannelProvider provider)
    { _engine = engine; _provider = provider; }
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var allChannels = await _engine.Session.GetAllChannelsAsync();
        _provider.RefreshCache(allChannels);

        var sessionEngine = _engine.GetActiveEnginesSnapshot()
            .OfType<AgentCoreProcessor.Engine.Session.SessionEngine>().FirstOrDefault();

        var arr = new JsonArray();
        foreach (var ch in allChannels)
        {
            var snap = sessionEngine?.GetChannelSessionSnapshot(ch.Id);
            var hasEngine = snap != null && snap.IsAlive;

            string engineStatus;
            if (hasEngine)
            {
                engineStatus = snap!.Type == "express" ? "Express" : "Working";
            }
            else
            {
                engineStatus = "未启动";
            }

            var msgCount = await _engine.Session.GetMessageCountByChannelAsync(ch.Id);
            arr.Add(new JsonObject
            {
                ["name"] = ch.Name,
                ["engine"] = engineStatus,
                ["messages"] = msgCount.ToString(),
                ["affinity"] = ch.Affinity.ToString("F1"),
                ["_link"] = $"/p/channels/{ch.Id}"
            });
        }
        return new DataResult { Data = arr, TotalCount = arr.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class ChannelInfoSource : IDataSource
{
    private readonly MasterEngine _engine;
    private readonly int _channelId;
    public ChannelInfoSource(MasterEngine engine, int channelId)
    { _engine = engine; _channelId = channelId; }
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var channel = await _engine.Session.GetChannelByIdAsync(_channelId);
        if (channel == null)
            return new DataResult { Data = new JsonObject { ["name"] = "未知频道" } };

        var config = ChannelStateManager.LoadConfig(_channelId, channel.Affinity);
        var msgCount = await _engine.Session.GetMessageCountByChannelAsync(_channelId);

        var sessionEngine = _engine.GetActiveEnginesSnapshot()
            .OfType<AgentCoreProcessor.Engine.Session.SessionEngine>().FirstOrDefault();
        var snap = sessionEngine?.GetChannelSessionSnapshot(_channelId);
        var hasEngine = snap != null && snap.IsAlive;

        string engineStatus;
        if (hasEngine)
        {
            engineStatus = snap!.Type == "express" ? "Express" : "Working";
        }
        else
        {
            engineStatus = "未启动";
        }

        // 提取状态改读 MindEngine 伴随提取器（全局攒批，非 per-channel 进度）
        var extractor = _engine.GetActiveEnginesSnapshot()
            .OfType<AgentCoreProcessor.Engine.Mind.MindEngine>().FirstOrDefault()?.Extract.Express;
        int pendingCount = extractor?.PendingCount ?? 0;
        long extractBatches = extractor?.ExtractCount ?? 0;

        var disabledActions = new JsonArray();
        if (!hasEngine) disabledActions.Add("view-engine");
        if (extractor == null || pendingCount == 0) disabledActions.Add("force-extraction");

        var data = new JsonObject
        {
            ["name"] = channel.Name,
            ["engine"] = engineStatus,
            ["affinity"] = channel.Affinity.ToString("F1"),
            ["importance"] = config.Importance,
            ["messages"] = msgCount.ToString(),
            ["extraction"] = $"全局攒批 {pendingCount} | 已提取 {extractBatches} 批",
            ["config"] = $"攒批阈值 {AgentCoreProcessor.Engine.Mind.ExpressExtractor.DefaultBatchSize}（全局）"
        };
        if (disabledActions.Count > 0)
            data["_disabled_actions"] = disabledActions;
        return new DataResult { Data = data };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var sessionEngine = _engine.GetActiveEnginesSnapshot()
            .OfType<AgentCoreProcessor.Engine.Session.SessionEngine>().FirstOrDefault();
        var snap = sessionEngine?.GetChannelSessionSnapshot(_channelId);
        var hasEngine = snap != null && snap.IsAlive;

        if (action == "view-engine")
        {
            if (hasEngine)
                return Task.FromResult(new ActionResult { Success = true, Message = $"/p/sessions/{snap!.SessionId}" });
            return Task.FromResult(new ActionResult { Success = false, Message = "无活跃会话" });
        }
        if (action == "force-extraction")
        {
            var extractor = _engine.GetActiveEnginesSnapshot()
                .OfType<AgentCoreProcessor.Engine.Mind.MindEngine>().FirstOrDefault()?.Extract.Express;
            if (extractor == null)
                return Task.FromResult(new ActionResult { Success = false, Message = "心智引擎未启动" });
            if (extractor.PendingCount == 0)
                return Task.FromResult(new ActionResult { Success = false, Message = "攒批为空，无待提取消息" });
            _ = extractor.FlushAllAsync();
            return Task.FromResult(new ActionResult { Success = true, Message = "已触发提取" });
        }
        return Task.FromResult(new ActionResult { Success = true });
    }
}

internal class ChannelMessagesSource : IDataSource
{
    private readonly MasterEngine _engine;
    private readonly int _channelId;
    public ChannelMessagesSource(MasterEngine engine, int channelId)
    { _engine = engine; _channelId = channelId; }
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 20;
        var offset = (page - 1) * pageSize;
        var keyword = query?.Search;

        var messages = await _engine.Session.SearchMessagesByChannelAsync(_channelId, keyword, offset, pageSize);
        var total = string.IsNullOrWhiteSpace(keyword)
            ? await _engine.Session.GetMessageCountByChannelAsync(_channelId)
            : messages.Count; // 搜索无独立计数的现成 API；至少不把关键词过滤后的当前页误报成全部频道消息

        var arr = new JsonArray();
        foreach (var msg in messages)
        {
            arr.Add(new JsonObject
            {
                ["time"] = TimeUtil.ToLocalSafe(msg.Time).ToString("MM-dd HH:mm:ss"),
                ["sender"] = msg.SenderName,
                ["content"] = msg.Content.Length > 200 ? msg.Content[..200] + "..." : msg.Content
            });
        }
        return new DataResult { Data = arr, TotalCount = total };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class TimerDisposable : IDisposable
{
    private readonly System.Threading.Timer _timer;
    public TimerDisposable(System.Threading.Timer timer) => _timer = timer;
    public void Dispose() => _timer.Dispose();
}
