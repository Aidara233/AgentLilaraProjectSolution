using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
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
            new() { Id = "channel-list", Source = new ChannelListSource(_engine, this) }
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
                Layout = new CardLayout { PreferredCols = 12 }
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
            new() { Id = "ch-info", Source = new ChannelInfoSource(_engine, channelId) },
            new() { Id = "ch-messages", Source = new ChannelMessagesSource(_engine, channelId) }
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

        var channelCheck = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
        var activeEngines = channelCheck?.GetActiveChannels();

        var arr = new JsonArray();
        foreach (var ch in allChannels)
        {
            ChannelEngine? eng = null;
            var hasEngine = activeEngines != null
                && activeEngines.TryGetValue(ch.Id, out eng) && eng.IsAlive;

            string engineStatus;
            if (hasEngine)
            {
                var snap = eng!.GetSnapshot();
                engineStatus = snap.IsBusy ? "运行" : "待机";
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

        var channelCheck = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
        ChannelEngine? eng = null;
        var hasEngine = channelCheck?.GetActiveChannels().TryGetValue(_channelId, out eng) == true && eng!.IsAlive;

        string engineStatus;
        bool extractionRunning = false;
        if (hasEngine)
        {
            var snap = eng!.GetSnapshot();
            engineStatus = snap.IsBusy ? "运行" : "待机";
            extractionRunning = snap.ExtractionRunning;
        }
        else
        {
            engineStatus = "未启动";
        }

        var disabledActions = new JsonArray();
        if (!hasEngine) disabledActions.Add("view-engine");
        if (!hasEngine || extractionRunning) disabledActions.Add("force-extraction");

        var data = new JsonObject
        {
            ["name"] = channel.Name,
            ["engine"] = engineStatus,
            ["affinity"] = channel.Affinity.ToString("F1"),
            ["importance"] = config.Importance,
            ["messages"] = msgCount.ToString(),
            ["extraction"] = extractionRunning
                ? $"提取中... ({channel.LastExtractedMessageId} / {msgCount})"
                : $"{channel.LastExtractedMessageId} / {msgCount}",
            ["config"] = $"活跃 {config.ActiveExtractionThreshold} | 潜水 {config.LurkingExtractionThreshold}"
        };
        if (disabledActions.Count > 0)
            data["_disabled_actions"] = disabledActions;
        return new DataResult { Data = data };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var channelCheck = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
        ChannelEngine? eng = null;
        var hasEngine = channelCheck?.GetActiveChannels().TryGetValue(_channelId, out eng) == true && eng!.IsAlive;

        if (action == "view-engine")
        {
            if (hasEngine)
                return Task.FromResult(new ActionResult { Success = true, Message = $"/p/engines/channel_{_channelId}" });
            return Task.FromResult(new ActionResult { Success = false, Message = "引擎未启动" });
        }
        if (action == "force-extraction")
        {
            if (!hasEngine)
                return Task.FromResult(new ActionResult { Success = false, Message = "引擎未启动" });
            if (eng!.GetSnapshot().ExtractionRunning)
                return Task.FromResult(new ActionResult { Success = false, Message = "提取正在进行中" });
            eng.ForceExtraction();
            return Task.FromResult(new ActionResult { Success = true, Message = "已触发强制提取" });
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
        var total = await _engine.Session.GetMessageCountByChannelAsync(_channelId);

        var arr = new JsonArray();
        foreach (var msg in messages)
        {
            arr.Add(new JsonObject
            {
                ["time"] = msg.Time.ToString("MM-dd HH:mm:ss"),
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
