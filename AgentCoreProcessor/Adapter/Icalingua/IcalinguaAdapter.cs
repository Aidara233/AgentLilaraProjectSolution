using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json.Linq;
using SocketIOClient;
using SocketIOClient.Common;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// Icalingua++ Bridge 适配器。经 Socket.IO 连接 oicq 桥接服务（Socket.IO 协议，非原生 WebSocket）。
    /// <para>
    /// 握手：收到 requireAuth(salt) → 用 ed25519 私钥签名 salt → emit auth(signature)。
    /// 登录判定：收到 onlineData 即视为已登录（Bridge 侧持 QQ 会话），记录 uin 为 bot 自身。
    /// 入站：addMessage {roomId, message}。出站：sendMessage RPC + 图片走 HTTP /api/{token}/sendMessage。
    /// </para>
    /// 定位：并行/备用通道（与 OneBot 共存）。实现 IChannelAwareAdapter.OwnsChannel 按 whitelist 分流。
    /// </summary>
    public class IcalinguaAdapter : IAdapter, IChannelAwareAdapter
    {
        public string Id { get; }
        public string Platform => "qq";
        public string? BotPlatformId => selfId > 0 ? selfId.ToString() : null;
        public event Action<IncomingMessage>? OnMessageReceived;

        private readonly IcalinguaConfig config;
        private SocketIO? io;
        private CancellationTokenSource? cts;

        private long selfId;
        private volatile bool authenticated;

        // 分层组件
        private readonly IcalinguaMessageParser parser;
        private readonly IcalinguaActions actions;

        internal HttpClient HttpClient { get; } = new();
        internal IcalinguaConfig Config => config;
        internal long SelfId => selfId;
        internal bool Authenticated => authenticated;
        internal IcalinguaActions Actions => actions;
        internal void SetSelfId(long id) => selfId = id;

        // 状态跟踪
        private AdapterConnectionState connectionState = AdapterConnectionState.Stopped;
        private DateTime? startedAt;
        private DateTime? lastMessageSentAt;
        private DateTime? lastMessageReceivedAt;
        private long messagesSent;
        private long messagesReceived;
        private int reconnectCount;
        private string? lastError;
        private DateTime? lastErrorAt;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> pendingAcks = new();

        public IcalinguaAdapter(string id, IcalinguaConfig config)
        {
            Id = id;
            this.config = config;
            parser = new IcalinguaMessageParser(this);
            actions = new IcalinguaActions(this);
            BuildSocket();
        }

        /// <summary>能否处理某频道（并行分流：仅当 whitelist/blacklist 命中）。</summary>
        public bool OwnsChannel(string channelId)
        {
            var mode = config.FilterMode.ToLowerInvariant();
            return mode switch
            {
                "whitelist" => config.Whitelist.Contains(channelId, StringComparer.OrdinalIgnoreCase),
                "blacklist" => !config.Blacklist.Contains(channelId, StringComparer.OrdinalIgnoreCase),
                _ => false
            };
        }

        // ── Socket.IO 生命周期 ──

        private void BuildSocket()
        {
            io?.Dispose();
            io = new SocketIO(new Uri(config.Server), new SocketIOOptions
            {
                EIO = EngineIO.V4,
                Transport = config.WebSocketOnly ? TransportProtocol.WebSocket : TransportProtocol.Polling,
                Reconnection = true,
                AutoUpgrade = !config.WebSocketOnly
            });

            io.OnConnected += (_, _) =>
            {
                connectionState = AdapterConnectionState.Connected;
                Signal.Event(LogGroup.Adapter, "Icalingua已连接", new { id = Id, server = config.Server });
            };
            io.OnDisconnected += (_, e) =>
            {
                connectionState = AdapterConnectionState.Reconnecting;
                Signal.Warn(LogGroup.Adapter, "Icalingua连接断开", new { id = Id, reason = e });
            };
            io.OnError += (_, e) =>
            {
                lastError = e;
                lastErrorAt = DateTime.Now;
                Signal.Error(LogGroup.Adapter, "Icalingua错误", new { id = Id, error = e });
            };
            io.OnReconnectAttempt += (_, _) => reconnectCount++;

            // ── 握手 ──
            io.On(IcalinguaProtocol.RequireAuth, response =>
            {
                var salt = response.GetValue<string>(0) ?? "";
                var seed = IcalinguaProtocol.ParseSeed(config.PrivateKey);
                if (seed == null)
                {
                    Signal.Error(LogGroup.Adapter, "Icalingua私钥无效，无法握手", new { id = Id });
                    return Task.CompletedTask;
                }
                var signature = IcalinguaProtocol.SignSalt(salt, seed);
                if (signature == null)
                {
                    Signal.Error(LogGroup.Adapter, "Icalingua签名失败", new { id = Id });
                    return Task.CompletedTask;
                }
                return io.EmitAsync(IcalinguaProtocol.Auth, new object[] { signature });
            });

            io.On(IcalinguaProtocol.AuthSucceed, _ =>
            {
                authenticated = true;
                Signal.Event(LogGroup.Adapter, "Icalingua认证成功", new { id = Id });
                return Task.CompletedTask;
            });
            io.On(IcalinguaProtocol.AuthFailed, _ =>
            {
                authenticated = false;
                lastError = "认证失败（私钥与 Bridge pubKey 不匹配）";
                lastErrorAt = DateTime.Now;
                connectionState = AdapterConnectionState.Error;
                Signal.Error(LogGroup.Adapter, "Icalingua认证失败", new { id = Id });
                return Task.CompletedTask;
            });

            // ── 登录态 ──
            io.On(IcalinguaProtocol.OnlineData, response =>
            {
                try
                {
                    var data = JsonToJObject(response, 0);
                    if (data?["uin"] is JToken uinTok)
                        selfId = uinTok.Value<long>();
                    authenticated = true;
                    Signal.Event(LogGroup.Adapter, "Icalingua已登录", new { id = Id, uin = selfId, online = data?["online"]?.Value<bool>() });
                }
                catch (Exception ex)
                {
                    Signal.Warn(LogGroup.Adapter, "onlineData 解析失败", new { id = Id, error = ex.Message });
                }
                return Task.CompletedTask;
            });

            // ── 入站消息 ──
            io.On(IcalinguaProtocol.AddMessage, async response =>
            {
                try
                {
                    var roomId = response.GetValue<long?>(0) ?? 0;
                    var msg = JsonToJObject(response, 1);
                    var parsed = await parser.HandleAddMessage(roomId, msg);
                    if (parsed != null)
                    {
                        Interlocked.Increment(ref messagesReceived);
                        lastMessageReceivedAt = DateTime.Now;
                        OnMessageReceived?.Invoke(parsed);
                    }
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Adapter, "Icalingua消息处理异常", new { id = Id, error = ex.Message });
                }
            });

            // 撤回等：Bridge 删除/更新，忽略展示（记录日志）
            io.On(IcalinguaProtocol.DeleteMessage, response =>
            {
                try
                {
                    var roomId = response.GetValue<long?>(0) ?? 0;
                    var messageId = response.GetValue<string>(1);
                    Signal.Event(LogGroup.Adapter, "Icalingua消息删除", new { id = Id, roomId, messageId });
                }
                catch { }
                return Task.CompletedTask;
            });

            io.OnAny((eventName, response) =>
            {
                // 其余事件仅记录，暂不处理（renewMessage/setAllRooms/updateRoom 等）
                if (eventName is not (IcalinguaProtocol.AddMessage or IcalinguaProtocol.OnlineData
                    or IcalinguaProtocol.RequireAuth or IcalinguaProtocol.AuthSucceed
                    or IcalinguaProtocol.AuthFailed or IcalinguaProtocol.DeleteMessage))
                {
                    Signal.Debug(LogGroup.Adapter, $"Icalingua事件: {eventName}");
                }
                return Task.CompletedTask;
            });
        }

        internal static JObject? JsonToJObject(IEventContext response, int index)
        {
            try
            {
                var el = response.GetValue<JsonElement>(index);
                return el.ValueKind == JsonValueKind.Null ? null : JObject.Parse(el.GetRawText());
            }
            catch
            {
                return null;
            }
        }

        // ── IAdapter ──

        public async Task StartAsync(CancellationToken ct = default)
        {
            startedAt = DateTime.Now;
            connectionState = AdapterConnectionState.Connecting;
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var parentCtx = SignalContext.Current;
            using var lifeCtx = Signal.Continue(
                SignalContext.NewSignalId(), parentCtx?.CurrentSpanId,
                $"adapter:{Id}", LogGroup.Adapter, "Icalingua适配器",
                new { id = Id, server = config.Server });

            authenticated = false;
            selfId = 0;

            try
            {
                await io!.ConnectAsync();
                connectionState = AdapterConnectionState.Connected;
                Signal.Event(LogGroup.Adapter, "Icalingua启动完成", new { id = Id, server = config.Server });
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Adapter, "Icalingua初始连接失败(将后台重连)", new { id = Id, error = ex.Message });
                lastError = ex.Message;
                lastErrorAt = DateTime.Now;
                connectionState = AdapterConnectionState.Reconnecting;
            }
        }

        public async Task StopAsync()
        {
            cts?.Cancel();
            foreach (var kv in pendingAcks)
                kv.Value.TrySetCanceled();
            pendingAcks.Clear();

            authenticated = false;
            try { if (io != null && io.Connected) await io.DisconnectAsync(); }
            catch { }
            connectionState = AdapterConnectionState.Stopped;
        }

        public AdapterStatus GetStatus() => new()
        {
            Id = Id,
            Platform = Platform,
            Enabled = connectionState != AdapterConnectionState.Stopped,
            State = connectionState,
            StartedAt = startedAt,
            LastMessageSentAt = lastMessageSentAt,
            LastMessageReceivedAt = lastMessageReceivedAt,
            MessagesSent = Interlocked.Read(ref messagesSent),
            MessagesReceived = Interlocked.Read(ref messagesReceived),
            ReconnectCount = reconnectCount,
            LastError = lastError,
            LastErrorAt = lastErrorAt
        };

        public Task<string?> SendMessageAsync(OutgoingMessage message) => actions.SendMessageAsync(message);

        internal void TrackSent() { Interlocked.Increment(ref messagesSent); lastMessageSentAt = DateTime.Now; }
        internal void TrackError(string error) { lastError = error; lastErrorAt = DateTime.Now; }

        // ── 通用操作接口 ──

        public List<AdapterAction> GetAvailableActions() => new()
        {
            new AdapterAction
            {
                Name = "get_groups", Label = "获取群列表", Description = "返回所有已加入的群",
                Params = new()
            },
            new AdapterAction
            {
                Name = "get_group_members", Label = "获取群成员", Description = "返回指定群成员列表",
                Params = new() { new ActionParam { Name = "group", Label = "群号", Type = "text" } }
            },
            new AdapterAction
            {
                Name = "delete_message", Label = "撤回消息", Description = "撤回指定消息",
                Params = new() { new ActionParam { Name = "room_id", Label = "房间ID(群为负数)", Type = "text" },
                                 new ActionParam { Name = "message_id", Label = "消息ID", Type = "text" } }
            },
            new AdapterAction
            {
                Name = "send_group_poke", Label = "戳一戳", Description = "戳一戳群内某人",
                Params = new() { new ActionParam { Name = "gin", Label = "群号", Type = "text" },
                                 new ActionParam { Name = "uin", Label = "目标QQ", Type = "text" } }
            },
            new AdapterAction
            {
                Name = "send_group_sign", Label = "群签到", Description = "群内签到",
                Params = new() { new ActionParam { Name = "gin", Label = "群号", Type = "text" } }
            },
        };

        public async Task<ActionResult> ExecuteActionAsync(string action, Dictionary<string, string> parameters)
        {
            try
            {
                switch (action)
                {
                    case "get_groups":
                        var groupsEl = await RpcElementAsync(IcalinguaProtocol.GetGroups);
                        bool groupsOk = groupsEl.ValueKind != JsonValueKind.Undefined;
                        return new ActionResult { Success = groupsOk, Result = groupsOk ? groupsEl.ToString() : "无返回" };

                    case "get_group_members":
                        if (!parameters.TryGetValue("group", out var group))
                            return new ActionResult { Success = false, Error = "缺少 group 参数" };
                        var membersEl = await RpcElementAsync(IcalinguaProtocol.GetGroupMembers, new object[] { group });
                        bool membersOk = membersEl.ValueKind != JsonValueKind.Undefined;
                        return new ActionResult { Success = membersOk, Result = membersOk ? membersEl.ToString() : "无返回" };

                    case "delete_message":
                        if (!parameters.TryGetValue("room_id", out var roomId) || !long.TryParse(roomId, out var rid))
                            return new ActionResult { Success = false, Error = "缺少 room_id 参数" };
                        if (!parameters.TryGetValue("message_id", out var msgId))
                            return new ActionResult { Success = false, Error = "缺少 message_id 参数" };
                        await EmitWithAckAsync(IcalinguaProtocol.DeleteMessageRpc, new object[] { rid, msgId });
                        return new ActionResult { Success = true };

                    case "send_group_poke":
                        if (!parameters.TryGetValue("gin", out var gin) || !long.TryParse(gin, out var g))
                            return new ActionResult { Success = false, Error = "缺少 gin 参数" };
                        if (!parameters.TryGetValue("uin", out var uin) || !long.TryParse(uin, out var u))
                            return new ActionResult { Success = false, Error = "缺少 uin 参数" };
                        await EmitWithAckAsync(IcalinguaProtocol.SendGroupPoke, new object[] { g, u });
                        return new ActionResult { Success = true };

                    case "send_group_sign":
                        if (!parameters.TryGetValue("gin", out var gin2) || !long.TryParse(gin2, out var g2))
                            return new ActionResult { Success = false, Error = "缺少 gin 参数" };
                        await EmitWithAckAsync(IcalinguaProtocol.SendGroupSign, new object[] { g2 });
                        return new ActionResult { Success = true };

                    default:
                        return new ActionResult { Success = false, Error = $"未实现动作: {action}" };
                }
            }
            catch (Exception ex)
            {
                return new ActionResult { Success = false, Error = ex.Message };
            }
        }

        // ── RPC 辅助（带 ack）──

        /// <summary>调用带回调的 RPC，返回首个 ack 数据（JsonElement）。超时/失败返回 default（ValueKind=Undefined）。</summary>
        internal async Task<JsonElement> RpcElementAsync(string eventName, IEnumerable<object>? args = null, int timeoutMs = 10000)
        {
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await io!.EmitAsync(eventName, args ?? Array.Empty<object>(), ack =>
                {
                    try { tcs.TrySetResult(ack.GetValue<JsonElement>(0)); }
                    catch { tcs.TrySetResult(default); }
                    return Task.CompletedTask;
                });
                return await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task
                    ? await tcs.Task
                    : default;
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Adapter, $"Icalingua RPC失败: {eventName}", new { id = Id, error = ex.Message });
                return default;
            }
        }

        /// <summary>调用返回字符串的 RPC（如 requestToken）。失败返回 null。</summary>
        internal async Task<string?> RpcStringAsync(string eventName, int timeoutMs = 10000)
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await io!.EmitAsync(eventName, Array.Empty<object>(), ack =>
                {
                    try { tcs.TrySetResult(ack.GetValue<string>(0)); }
                    catch { tcs.TrySetResult(null); }
                    return Task.CompletedTask;
                });
                return await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task
                    ? await tcs.Task
                    : null;
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Adapter, $"Icalingua RPC失败: {eventName}", new { id = Id, error = ex.Message });
                return null;
            }
        }

        internal async Task EmitWithAckAsync(string eventName, IEnumerable<object>? args = null)
        {
            try
            {
                await io!.EmitAsync(eventName, args ?? Array.Empty<object>());
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Adapter, $"Icalingua emission失败: {eventName}", new { id = Id, error = ex.Message });
            }
        }
    }
}
