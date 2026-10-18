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
using AgentCoreProcessor.Util;
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
                LinkEvent = "send-config",
                Schema = new FormSchema
                {
                    Fields = new()
                    {
                        new() { Field = "mode", Label = "模式", Type = FormFieldType.Select, Required = true,
                            DefaultValue = "user",
                            Options = new()
                            {
                                new() { Value = "user", Label = "模拟用户" },
                                new() { Value = "bot", Label = "替 Bot 说话" },
                                new() { Value = "custom", Label = "自定义发送者" },
                            }
                        },
                        new() { Field = "botSaveMode", Label = "入库策略", Type = FormFieldType.Select,
                            DefaultValue = "both",
                            Options = new()
                            {
                                new() { Value = "both", Label = "发送并入库" },
                                new() { Value = "adapter_only", Label = "仅发适配器（不入库）" },
                                new() { Value = "db_only", Label = "仅入库（不发适配器）" },
                            }
                        },
                        new() { Field = "simulateMention", Label = "模拟 @提及", Type = FormFieldType.Toggle, DefaultValue = "false" },
                        new() { Field = "simulatePrivate", Label = "模拟私聊", Type = FormFieldType.Toggle, DefaultValue = "false" },
                        new() { Field = "customName", Label = "自定义名称", Placeholder = "发送者显示名称", DefaultValue = "TestUser" },
                    },
                    ShowReset = false, ShowSubmit = false,
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
                    },
                },
                Layout = new CardLayout { PreferredCols = 3, MinWidth = "240px", GridColumnStart = 1 },
            },
            new()
            {
                Id = "console-chat", Type = CardType.Chat, Title = "实时对话",
                DataSourceId = "console-chat", ListenEvent = "console-channel-selected",
                InjectEvent = "send-config",
                Schema = new ChatSchema { ShowSenderSwitch = false, ShowInput = true },
                Layout = new CardLayout { PreferredCols = 9, MinWidth = "400px", Height = "calc(100vh - 160px)", GridColumnStart = 4 },
            },
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "console-channels", Factory = () => new ConsoleChannelSource(_engine) },
            new() { Id = "console-engine-ctl", Factory = () => new ConsoleEngineSource(_engine) },
            new() { Id = "console-chat", Factory = () => new ConsoleChatSource(_engine) },
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
        var sessionEngine = _engine.GetActiveEnginesSnapshot()
            .OfType<AgentCoreProcessor.Engine.Session.SessionEngine>().FirstOrDefault();

        var arr = new JsonArray();
        foreach (var ch in channels.OrderByDescending(c => c.Id))
        {
            var hasEngine = sessionEngine?.GetChannelSessionSnapshot(ch.Id)?.IsAlive == true;
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
                    ["_disabled_actions"] = new JsonArray { "force-start", "force-stop" },
                }
            };
        }

        var sessionEngine = _engine.GetActiveEnginesSnapshot()
            .OfType<AgentCoreProcessor.Engine.Session.SessionEngine>().FirstOrDefault();
        var hasEngine = sessionEngine?.GetChannelSessionSnapshot(_selectedChannelId)?.IsAlive == true;
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
                    : new JsonArray { "force-stop" },
            }
        };
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        return action switch
        {
            "force-start" => await ForceStart(),
            "force-stop" => await ForceStop(),
            _ => new ActionResult { Success = false, Message = $"未知操作: {action}" },
        };
    }

    private async Task<ActionResult> ForceStart()
    {
        if (_selectedChannelId == 0) return Fail("未选择频道");
        var sessionEngine = _engine.GetActiveEnginesSnapshot()
            .OfType<AgentCoreProcessor.Engine.Session.SessionEngine>().FirstOrDefault();
        if (sessionEngine == null) return Fail("SessionEngine 未运行");
        if (sessionEngine.GetChannelSessionSnapshot(_selectedChannelId)?.IsAlive == true)
            return new ActionResult { Success = true, Message = "频道已有活跃会话" };

        var ch = await _engine.Session.GetChannelByIdAsync(_selectedChannelId);
        if (ch == null) return Fail($"频道 #{_selectedChannelId} 不存在");

        var session = sessionEngine.CreateExpressSessionForChannel(_selectedChannelId);
        return session == null
            ? new ActionResult { Success = true, Message = "频道已有活跃会话" }
            : new ActionResult { Success = true, Message = "express 会话已创建" };
    }

    private async Task<ActionResult> ForceStop()
    {
        if (_selectedChannelId == 0) return Fail("未选择频道");
        var sessionEngine = _engine.GetActiveEnginesSnapshot()
            .OfType<AgentCoreProcessor.Engine.Session.SessionEngine>().FirstOrDefault();
        if (sessionEngine == null) return Fail("SessionEngine 未运行");
        var snap = sessionEngine.GetChannelSessionSnapshot(_selectedChannelId);
        if (snap?.IsAlive == true)
        {
            var stop = sessionEngine.StopSession(snap.SessionId, "console force-stop");
            if (stop.Success)
                return new ActionResult { Success = true, Message = "会话已请求停止" };

            var reason = stop.BlockReason == "Has running children"
                ? $"存在活跃子会话：{string.Join(", ", stop.BlockingChildren ?? new List<string>())}"
                : stop.BlockReason ?? "未知原因";
            return Fail($"停止失败：{reason}");
        }
        return Fail("无活跃会话");
    }

    private static ActionResult Fail(string msg) => new() { Success = false, Message = msg };
}

internal class ConsoleChatSource : IDataSource
{
    private readonly MasterEngine _engine;
    private int _channelId;
    private string _channelName = "";
    private int _cachedChannelId;
    private int _lastMessageId;

    public bool SupportsPush => true;

    public ConsoleChatSource(MasterEngine engine) { _engine = engine; }

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            if (_channelId <= 0) return;
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
        }, null, 2000, 2000);

        // 清理闭包捕获自己的 timer，避免旧订阅释放时误杀新订阅。
        return new ActionDisposable(() =>
        {
            timer?.Dispose();
            timer = null;
        });
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        if (query?.Extra is JsonObject extra)
        {
            var id = (int?)extra["channelId"];
            if (id.HasValue && id.Value != _channelId)
            {
                _channelId = id.Value;
                _channelName = "";
                _cachedChannelId = 0;
                _lastMessageId = 0;
            }
        }

        if (_channelId == 0)
            return new DataResult { Data = new JsonArray() };

        if (_cachedChannelId != _channelId || string.IsNullOrEmpty(_channelName))
        {
            var channels = await _engine.Session.GetAllChannelsAsync();
            _channelName = channels.FirstOrDefault(c => c.Id == _channelId)?.Name ?? "";
            _cachedChannelId = _channelId;
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
                ["time"] = TimeUtil.ToLocalSafe(m.Time).ToString("HH:mm:ss"),
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

        var mode = payload["mode"]?.ToString() ?? "user";
        var botSaveMode = payload["botSaveMode"]?.ToString() ?? "both";
        var simulateMention = payload["simulateMention"]?.ToString() == "true";
        var simulatePrivate = payload["simulatePrivate"]?.ToString() == "true";
        var customName = payload["customName"]?.ToString() ?? "TestUser";

        // 频道名称缓存必须与当前选中频道绑定，切换频道后重新解析。
        if (_cachedChannelId != _channelId || string.IsNullOrEmpty(_channelName))
        {
            var channels = await _engine.Session.GetAllChannelsAsync();
            _channelName = channels.FirstOrDefault(c => c.Id == _channelId)?.Name ?? "";
            _cachedChannelId = _channelId;
        }
        if (string.IsNullOrEmpty(_channelName))
            return new ActionResult { Success = false, Message = $"频道 #{_channelId} 不存在" };

        try
        {
            switch (mode)
            {
                case "bot":
                    await SendAsBotAsync(text, botSaveMode, ct);
                    break;
                case "custom":
                    await SendAsCustomAsync(text, customName, simulateMention, simulatePrivate, ct);
                    break;
                default:
                    await SendAsUserAsync(text, simulateMention, simulatePrivate, ct);
                    break;
            }

            return new ActionResult { Success = true, Message = "已发送" };
        }
        catch (OperationCanceledException)
        {
            return new ActionResult { Success = false, Message = "发送已取消" };
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Message = $"发送失败: {ex.Message}" };
        }
    }

    /// <summary>模拟用户入向消息：走真实路由（无会话时会孵化 express）。等待处理完成再返回。</summary>
    private async Task SendAsUserAsync(string text, bool mention, bool priv, CancellationToken ct)
    {
        await _engine.RouteMessageAsync(new IncomingMessage
        {
            Platform = "WebConsole", PlatformUserId = "web-admin",
            ChannelId = _channelName, Content = text,
            DisplayName = "WebAdmin", Time = DateTime.Now,
            IsMentioned = mention, IsPrivate = priv,
        });
    }

    /// <summary>
    /// 替 Bot 说话：直接走适配器出向。
    /// both = 先入库再发送；adapter_only 只发送；db_only 只入库。
    /// 注意这里不是 RouteMessageAsync（那是用户入向入口，会触发模型回复）。
    /// </summary>
    private async Task SendAsBotAsync(string text, string saveMode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var adapter = _engine.GetActiveEnginesSnapshot()
            .OfType<AgentCoreProcessor.Engine.AdapterEng.AdapterEngine>().FirstOrDefault()
            ?.ResolveAdapter(_channelName);

        if (saveMode is "both" or "adapter_only" && adapter == null)
            throw new InvalidOperationException("没有可用的出向适配器");

        if (saveMode is "both" or "adapter_only")
        {
            var outgoing = new OutgoingMessage
            {
                ChannelId = _channelName,
                Content = text,
            };
            adapter!.SendMessageAsync(outgoing).GetAwaiter().GetResult();
        }

        if (saveMode is "both" or "db_only")
        {
            await _engine.Session.SaveBotMessageAsync(_channelId, text, null);
        }
    }

    private async Task SendAsCustomAsync(string text, string name, bool mention, bool priv, CancellationToken ct)
    {
        await _engine.RouteMessageAsync(new IncomingMessage
        {
            Platform = "WebConsole", PlatformUserId = $"custom-{name.ToLowerInvariant()}",
            ChannelId = _channelName, Content = text,
            DisplayName = name, Time = DateTime.Now,
            IsMentioned = mention, IsPrivate = priv,
        });
    }
}
