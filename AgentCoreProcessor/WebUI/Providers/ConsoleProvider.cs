using AgentLilara.PluginSDK.WebUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using ActionResult = AgentLilara.PluginSDK.WebUI.ActionResult;

namespace AgentCoreProcessor.WebUI.Providers;

internal class ConsoleProvider : IWebUIProvider
{
    private readonly MasterEngine _engine;

    public string Id => "console";
    public string DisplayName => "控制台";
    public IReadOnlyList<PageDefinition> Pages => _pages;
    private readonly List<PageDefinition> _pages;

    public ConsoleProvider(MasterEngine engine)
    {
        _engine = engine;
        _pages = new List<PageDefinition> { BuildPage() };
    }

    private PageDefinition BuildPage() => new()
    {
        Route = "channels/console",
        LayoutType = PageLayoutType.Sidebar,
        Meta = new PageMeta
        {
            Title = "频道控制台",
            Icon = "bi-terminal",
            Group = "频道引擎",
            Order = 50,
        },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "console-ch-list", Type = CardType.Table, Title = "频道",
                DataSourceId = "console-channels", LinkEvent = "console-channel-selected",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "name", Header = "频道" },
                        new() { Field = "engine", Header = "引擎", Format = ColumnFormat.Badge },
                    },
                    Searchable = true, Paginated = false,
                },
                Layout = new CardLayout { PreferredCols = 3, MinWidth = "240px", GridColumnStart = 1 },
            },
            new()
            {
                Id = "console-send-mode", Type = CardType.Form, Title = "发送模式",
                DataSourceId = "console-send-config",
                Schema = new FormSchema
                {
                    Fields = new()
                    {
                        new() { Field = "mode", Label = "模式", Type = FormFieldType.Select, Required = true,
                            Options = new()
                            {
                                new() { Value = "user", Label = "模拟用户" },
                                new() { Value = "bot", Label = "替 Bot 说话" },
                                new() { Value = "custom", Label = "自定义发送者" },
                            }
                        },
                        new() { Field = "botSaveMode", Label = "入库策略", Type = FormFieldType.Select,
                            Options = new()
                            {
                                new() { Value = "both", Label = "发送并入库" },
                                new() { Value = "adapter_only", Label = "仅发适配器（不入库）" },
                                new() { Value = "db_only", Label = "仅入库（不发适配器）" },
                            }
                        },
                        new() { Field = "simulateMention", Label = "模拟 @提及", Type = FormFieldType.Toggle },
                        new() { Field = "simulatePrivate", Label = "模拟私聊", Type = FormFieldType.Toggle },
                        new() { Field = "customName", Label = "自定义名称", Placeholder = "发送者显示名称" },
                    },
                    ShowReset = false,
                },
                Layout = new CardLayout { PreferredCols = 3, MinWidth = "240px", GridColumnStart = 1 },
            },
            new()
            {
                Id = "console-engine-ctl", Type = CardType.Status, Title = "引擎控制",
                DataSourceId = "console-engine-ctl", ListenEvent = "console-channel-selected",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "channelName", Label = "频道" },
                        new() { Field = "engineState", Label = "引擎状态", Type = StatusFieldType.Badge },
                        new() { Field = "messageCount", Label = "消息总数" },
                    },
                    Actions = new()
                    {
                        new() { Id = "force-start", Label = "强制启动", Icon = "bi-play-fill" },
                        new() { Id = "force-stop", Label = "强制停止", Icon = "bi-stop-fill", Danger = true },
                        new() { Id = "force-compress", Label = "强制压缩", Icon = "bi-arrow-down-up" },
                    },
                },
                Layout = new CardLayout { PreferredCols = 3, MinWidth = "240px", GridColumnStart = 1 },
            },
            new()
            {
                Id = "console-chat", Type = CardType.Chat, Title = "实时对话",
                DataSourceId = "console-chat", ListenEvent = "console-channel-selected",
                Schema = new ChatSchema { ShowSenderSwitch = false, ShowInput = true },
                Layout = new CardLayout { PreferredCols = 9, MinWidth = "400px", Height = "calc(100vh - 160px)", GridColumnStart = 4 },
            },
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "console-channels", Source = new ConsoleChannelSource(_engine) },
            new() { Id = "console-send-config", Source = new SendConfigSource() },
            new() { Id = "console-engine-ctl", Source = new ConsoleEngineSource(_engine) },
            new() { Id = "console-chat", Source = new ConsoleChatSource(_engine) },
        },
    };
}

// ---- Data Sources ----

internal class ConsoleChannelSource : IDataSource
{
    private readonly MasterEngine _engine;
    public bool SupportsPush => true;

    public ConsoleChannelSource(MasterEngine engine) { _engine = engine; }

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        void OnChanged() => callback(null);
        var timer = new Timer(_ => OnChanged(), null, 3000, 3000);
        return new ActionDisposable(() => timer.Dispose());
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var channels = await _engine.Session.GetAllChannelsAsync();
        var check = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
        var active = check?.GetActiveChannels();

        var arr = new JsonArray();
        foreach (var ch in channels.OrderByDescending(c => c.Id))
        {
            var hasEngine = active?.ContainsKey(ch.Id) == true;
            arr.Add(new JsonObject
            {
                ["name"] = $"#{ch.Id} {ch.Name}",
                ["engine"] = hasEngine ? "● 活跃" : "○ 空闲",
                ["channelId"] = ch.Id,
                ["channelName"] = ch.Name,
            });
        }

        return new DataResult { Data = arr, TotalCount = arr.Count };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false, Message = "不支持" });
}

internal class SendConfigSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        return Task.FromResult(new DataResult
        {
            Data = new JsonObject
            {
                ["mode"] = "user", ["botSaveMode"] = "both",
                ["simulateMention"] = false, ["simulatePrivate"] = false,
                ["customName"] = "TestUser",
            }
        });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (data is JsonObject obj) _lastConfig = obj;
        return Task.FromResult(new ActionResult { Success = true, Message = "已更新" });
    }

    private static JsonObject? _lastConfig;
    public static JsonObject? LastConfig => _lastConfig;
}

internal class ConsoleEngineSource : IDataSource
{
    private readonly MasterEngine _engine;
    private int _selectedChannelId;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public ConsoleEngineSource(MasterEngine engine) { _engine = engine; }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        if (query?.Extra is JsonObject extra)
            _selectedChannelId = (int?)extra["channelId"] ?? 0;

        if (_selectedChannelId == 0)
        {
            return new DataResult
            {
                Data = new JsonObject
                {
                    ["channelName"] = "未选择", ["engineState"] = "-", ["messageCount"] = "-",
                    ["_disabled_actions"] = new JsonArray { "force-start", "force-stop", "force-compress" },
                }
            };
        }

        var check = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
        var active = check?.GetActiveChannels();
        var hasEngine = active?.TryGetValue(_selectedChannelId, out var chEngine) == true && chEngine.IsAlive;
        var msgCount = await _engine.Session.GetMessageCountByChannelAsync(_selectedChannelId);

        return new DataResult
        {
            Data = new JsonObject
            {
                ["channelName"] = $"#{_selectedChannelId}",
                ["engineState"] = hasEngine ? "● 运行中" : "○ 已停止",
                ["messageCount"] = msgCount,
                ["_disabled_actions"] = hasEngine
                    ? new JsonArray { "force-start" }
                    : new JsonArray { "force-stop", "force-compress" },
            }
        };
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        return action switch
        {
            "force-start" => await ForceStart(),
            "force-stop" => await ForceStop(),
            "force-compress" => await ForceCompress(),
            _ => new ActionResult { Success = false, Message = $"未知操作: {action}" },
        };
    }

    private async Task<ActionResult> ForceStart()
    {
        if (_selectedChannelId == 0) return Fail("未选择频道");
        var check = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
        var active = check?.GetActiveChannels();
        if (active?.ContainsKey(_selectedChannelId) == true)
            return new ActionResult { Success = true, Message = "引擎已在运行" };

        var channels = await _engine.Session.GetAllChannelsAsync();
        var ch = channels.FirstOrDefault(c => c.Id == _selectedChannelId);
        if (ch == null) return Fail($"频道 #{_selectedChannelId} 不存在");
        var channelName = ch.Name;

        var msg = new IncomingMessage
        {
            Platform = "WebConsole", PlatformUserId = "web-admin",
            ChannelId = channelName, Content = "(控制台手动启动)",
            DisplayName = "WebAdmin", Time = DateTime.Now,
        };
        await Task.Run(() => _engine.EventBus.PublishMessage(msg));
        await Task.Delay(500);
        return new ActionResult { Success = true, Message = "引擎启动信号已发送" };
    }

    private Task<ActionResult> ForceStop()
    {
        if (_selectedChannelId == 0) return Task.FromResult(Fail("未选择频道"));
        var check = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
        var active = check?.GetActiveChannels();
        if (active?.TryGetValue(_selectedChannelId, out var chEngine) == true && chEngine.IsAlive)
        {
            chEngine.RequestStop();
            return Task.FromResult(new ActionResult { Success = true, Message = "引擎已请求停止" });
        }
        return Task.FromResult(Fail("引擎未运行"));
    }

    private Task<ActionResult> ForceCompress()
    {
        if (_selectedChannelId == 0) return Task.FromResult(Fail("未选择频道"));
        var check = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
        var active = check?.GetActiveChannels();
        if (active?.TryGetValue(_selectedChannelId, out var chEngine) == true && chEngine.IsAlive)
        {
            chEngine.SignalGate();
            return Task.FromResult(new ActionResult { Success = true, Message = "压缩信号已发送" });
        }
        return Task.FromResult(Fail("引擎未运行"));
    }

    private static ActionResult Fail(string msg) => new() { Success = false, Message = msg };
}

internal class ConsoleChatSource : IDataSource
{
    private readonly MasterEngine _engine;
    private int _channelId;
    private string _channelName = "";
    private Timer? _pollTimer;
    private int _lastMessageId;
    private Action<JsonNode?>? _callback;

    public bool SupportsPush => true;

    public ConsoleChatSource(MasterEngine engine) { _engine = engine; }

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        _callback = callback;
        _pollTimer = new Timer(_ =>
        {
            if (_channelId > 0)
            {
                try
                {
                    var msgs = _engine.Session.GetContextByChannelAsync(_channelId, 1).Result;
                    if (msgs.Count > 0 && msgs[0].Id != _lastMessageId)
                    {
                        _lastMessageId = msgs[0].Id;
                        callback(null);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[ConsoleProvider] 轮询失败: {ex.Message}"); }
            }
        }, null, 2000, 2000);
        return new ActionDisposable(() => { _pollTimer?.Dispose(); _callback = null; });
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        if (query?.Extra is JsonObject extra)
            _channelId = (int?)extra["channelId"] ?? _channelId;

        if (_channelId == 0)
            return new DataResult { Data = new JsonArray() };

        // 缓存频道真实名称
        if (string.IsNullOrEmpty(_channelName))
        {
            var channels = await _engine.Session.GetAllChannelsAsync();
            _channelName = channels.FirstOrDefault(c => c.Id == _channelId)?.Name ?? "";
        }

        var messages = await _engine.Session.GetContextByChannelAsync(_channelId, 50);
        _lastMessageId = messages.FirstOrDefault()?.Id ?? 0;

        var arr = new JsonArray();
        foreach (var m in messages)
        {
            var isBot = m.IsFromBot;
            arr.Add(new JsonObject
            {
                ["sender"] = isBot ? "Lilara" : (string.IsNullOrEmpty(m.SenderName) ? "用户" : m.SenderName),
                ["text"] = m.Content ?? "",
                ["time"] = m.Time.ToString("HH:mm:ss"),
                ["isBot"] = isBot,
                ["role"] = isBot ? "assistant" : "user",
            });
        }

        return new DataResult { Data = arr };
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action != "send" || _channelId == 0 || data is not JsonObject payload)
            return new ActionResult { Success = false, Message = "无效请求" };

        var text = payload["text"]?.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return new ActionResult { Success = false, Message = "消息为空" };

        var config = SendConfigSource.LastConfig;
        var mode = config?["mode"]?.ToString() ?? "user";
        var botSaveMode = config?["botSaveMode"]?.ToString() ?? "both";
        var simulateMention = config?["simulateMention"]?.GetValue<bool>() ?? false;
        var simulatePrivate = config?["simulatePrivate"]?.GetValue<bool>() ?? false;
        var customName = config?["customName"]?.ToString() ?? "TestUser";

        // 确保频道名称已缓存
        if (string.IsNullOrEmpty(_channelName))
        {
            var channels = await _engine.Session.GetAllChannelsAsync();
            _channelName = channels.FirstOrDefault(c => c.Id == _channelId)?.Name ?? "";
        }

        try
        {
            // 自动启动引擎
            var check = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
            var active = check?.GetActiveChannels();
            if (active?.ContainsKey(_channelId) != true)
            {
                var kickMsg = new IncomingMessage
                {
                    Platform = "WebConsole", PlatformUserId = "web-admin",
                    ChannelId = _channelName, Content = "(控制台自动启动)",
                    DisplayName = "WebAdmin", Time = DateTime.Now,
                };
                await Task.Run(() => _engine.EventBus.PublishMessage(kickMsg));
                await Task.Delay(800);
            }

            await Task.Run(() =>
            {
                switch (mode)
                {
                    case "bot": SendAsBot(text, botSaveMode); break;
                    case "custom": SendAsCustom(text, customName, simulateMention, simulatePrivate); break;
                    default: SendAsUser(text, simulateMention, simulatePrivate); break;
                }
            });

            return new ActionResult { Success = true, Message = "已发送" };
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Message = $"发送失败: {ex.Message}" };
        }
    }

    private void SendAsUser(string text, bool mention, bool priv)
    {
        _engine.EventBus.PublishMessage(new IncomingMessage
        {
            Platform = "WebConsole", PlatformUserId = "web-admin",
            ChannelId = _channelName, Content = text,
            DisplayName = "WebAdmin", Time = DateTime.Now,
            IsMentioned = mention, IsPrivate = priv,
        });
    }

    private void SendAsBot(string text, string saveMode)
    {
        if (saveMode is "both" or "adapter_only")
        {
            _engine.EventBus.PublishMessage(new IncomingMessage
            {
                Platform = "WebConsole", PlatformUserId = "bot-self",
                ChannelId = _channelName, Content = text,
                DisplayName = "Lilara", Time = DateTime.Now,
            });
        }

        if (saveMode is "both" or "db_only")
        {
            _engine.Session.SaveBotMessageAsync(_channelId, text, null).GetAwaiter().GetResult();
        }
    }

    private void SendAsCustom(string text, string name, bool mention, bool priv)
    {
        _engine.EventBus.PublishMessage(new IncomingMessage
        {
            Platform = "WebConsole", PlatformUserId = $"custom-{name.ToLowerInvariant()}",
            ChannelId = _channelName, Content = text,
            DisplayName = name, Time = DateTime.Now,
            IsMentioned = mention, IsPrivate = priv,
        });
    }
}
