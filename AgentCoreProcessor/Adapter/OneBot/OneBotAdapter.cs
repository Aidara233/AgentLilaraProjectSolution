using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Adapter
{
    public class OneBotAdapter : IAdapter
    {
        public string Id { get; }
        public string Platform => "qq";
        public string? BotPlatformId => selfId > 0 ? selfId.ToString() : null;
        public event Action<IncomingMessage>? OnMessageReceived;

        private readonly string? configPath;
        private OneBotConfig config;
        private ClientWebSocket? ws;
        private CancellationTokenSource? cts;
        private long selfId;

        internal HttpClient HttpClient { get; } = new();
        internal OneBotConfig Config => config;
        internal long SelfId => selfId;

        private int echoCounter;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> pendingCalls = new();

        private readonly HashSet<long> sentMessageIds = new();
        private const int MaxSentMessageIds = 200;
        private const int MaxReconnectDelayMs = 30000;

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

        // 分层组件
        private readonly OneBotMessageParser parser;
        private readonly OneBotActions actions;

        internal OneBotActions Actions => actions;

        public OneBotAdapter(string id, OneBotConfig config)
        {
            Id = id;
            this.config = config;
            this.configPath = null;
            parser = new OneBotMessageParser(this);
            actions = new OneBotActions(this);
        }

        public OneBotAdapter(string configPath)
        {
            Id = "qq-legacy";
            this.configPath = configPath;
            this.config = new OneBotConfig();
            parser = new OneBotMessageParser(this);
            actions = new OneBotActions(this);
        }

        private Task? receiveTask;

        public async Task StartAsync(CancellationToken ct = default)
        {
            if (configPath != null && File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                config = JsonConvert.DeserializeObject<OneBotConfig>(json) ?? new OneBotConfig();
            }

            startedAt = DateTime.Now;
            connectionState = AdapterConnectionState.Connecting;
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var parentCtx = SignalContext.Current;
            using var lifeCtx = Signal.Continue(
                parentCtx?.SignalId ?? Signal.NewId(), parentCtx?.CurrentSpanId,
                $"adapter:onebot:{Id}", LogGroup.Adapter, "OneBot适配器",
                new { id = Id, wsUrl = config.WsUrl });

            try
            {
                await ConnectAsync(cts.Token);
                connectionState = AdapterConnectionState.Connected;
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Adapter, "OneBot初始连接失败", new { error = ex.Message });
                connectionState = AdapterConnectionState.Reconnecting;
                lastError = ex.Message;
                lastErrorAt = DateTime.Now;
            }

            receiveTask = RunReceiveLoopWithReconnectAsync(cts.Token);

            if (ws?.State == WebSocketState.Open)
            {
                try
                {
                    selfId = await GetSelfIdAsync();
                }
                catch (Exception ex)
                {
                    Signal.Warn(LogGroup.Adapter, "获取Bot ID失败", new { error = ex.Message });
                }
            }
        }

        public Task WaitAsync() => receiveTask ?? Task.CompletedTask;

        public async Task StopAsync()
        {
            cts?.Cancel();

            foreach (var kv in pendingCalls)
                kv.Value.TrySetCanceled();
            pendingCalls.Clear();

            if (ws != null && ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "停止", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { }
            }
            ws?.Dispose();
            ws = null;

            connectionState = AdapterConnectionState.Stopped;
        }

        public Task ReloadConfigAsync()
        {
            if (configPath == null || !File.Exists(configPath))
                return Task.CompletedTask;

            var json = File.ReadAllText(configPath);
            var newConfig = JsonConvert.DeserializeObject<OneBotConfig>(json) ?? new OneBotConfig();

            bool connectionChanged = newConfig.WsUrl != config.WsUrl || newConfig.Token != config.Token;
            config = newConfig;

            return Task.CompletedTask;
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

        internal OneBotConfig GetConfig() => config;

        public Task<string?> SendMessageAsync(OutgoingMessage message) => actions.SendMessageAsync(message);

        // ── 内部辅助（供 Parser/Actions 使用）──

        internal bool IsSentMessage(long messageId)
        {
            lock (sentMessageIds)
            {
                return sentMessageIds.Contains(messageId);
            }
        }

        internal void TrackSentMessage(long messageId)
        {
            lock (sentMessageIds)
            {
                if (sentMessageIds.Count >= MaxSentMessageIds)
                    sentMessageIds.Clear();
                sentMessageIds.Add(messageId);
            }
            Interlocked.Increment(ref messagesSent);
            lastMessageSentAt = DateTime.Now;
        }

        // ── WebSocket 连接 ──

        private async Task ConnectAsync(CancellationToken ct)
        {
            ws?.Dispose();
            ws = new ClientWebSocket();

            if (!string.IsNullOrEmpty(config.Token))
                ws.Options.SetRequestHeader("Authorization", $"Bearer {config.Token}");

            await ws.ConnectAsync(new Uri(config.WsUrl), ct).ConfigureAwait(false);
        }

        private async Task RunReceiveLoopWithReconnectAsync(CancellationToken ct)
        {
            int reconnectDelayMs = 1000;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ReceiveLoopAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Adapter, "OneBot接收循环异常，即将重连", new { error = ex.Message });
                }

                if (ct.IsCancellationRequested) break;

                connectionState = AdapterConnectionState.Reconnecting;
                try
                {
                    await Task.Delay(reconnectDelayMs, ct);
                }
                catch (OperationCanceledException) { break; }

                try
                {
                    await ConnectAsync(ct);
                    try { selfId = await GetSelfIdAsync(); }
                    catch { selfId = 0; }
                    connectionState = AdapterConnectionState.Connected;
                    reconnectCount++;
                    reconnectDelayMs = 1000;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Signal.Warn(LogGroup.Adapter, "OneBot重连失败", new { error = ex.Message, retryMs = reconnectDelayMs });
                    lastError = ex.Message;
                    lastErrorAt = DateTime.Now;
                    reconnectDelayMs = Math.Min(reconnectDelayMs * 2, MaxReconnectDelayMs);
                }
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];

            while (!ct.IsCancellationRequested && ws?.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());

                JObject data;
                try { data = JObject.Parse(json); }
                catch { continue; }

                var echo = data["echo"]?.ToString();
                if (echo != null && pendingCalls.TryRemove(echo, out var tcs))
                {
                    tcs.TrySetResult(data);
                }
                else
                {
                    HandleEvent(data);
                }
            }
        }

        private async void HandleEvent(JObject data)
        {
            var postType = data["post_type"]?.ToString();
            if (postType != null && postType != "meta_event")
            {
                var raw = data.ToString(Formatting.None);
            }

            var msg = await parser.HandleEventAsync(data);
            if (msg != null)
            {
                Interlocked.Increment(ref messagesReceived);
                lastMessageReceivedAt = DateTime.Now;
                OnMessageReceived?.Invoke(msg);
            }
        }

        // ── API 调用 ──

        internal async Task<JObject?> CallApiAsync(string action, JObject? param = null)
        {
            if (ws?.State != WebSocketState.Open) return null;

            var echo = Interlocked.Increment(ref echoCounter).ToString();
            var request = new JObject
            {
                ["action"] = action,
                ["params"] = param ?? new JObject(),
                ["echo"] = echo
            };

            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingCalls[echo] = tcs;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(request.ToString(Formatting.None));
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                    cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(10000)).ConfigureAwait(false);
                if (completed == tcs.Task)
                    return await tcs.Task;

                pendingCalls.TryRemove(echo, out _);
                return null;
            }
            catch
            {
                pendingCalls.TryRemove(echo, out _);
                return null;
            }
        }

        private async Task<long> GetSelfIdAsync()
        {
            var resp = await CallApiAsync("get_login_info");
            return resp?["data"]?["user_id"]?.Value<long>() ?? 0;
        }

        // ── 通用操作接口 ──

        public List<AdapterAction> GetAvailableActions() => new()
        {
            new AdapterAction
            {
                Name = "get_group_list", Label = "获取群列表", Description = "返回所有已加入的群",
                Params = new()
            },
            new AdapterAction
            {
                Name = "get_group_member_list", Label = "获取群成员列表", Description = "返回指定群的成员列表",
                Params = new() { new ActionParam { Name = "group_id", Label = "群号", Type = "text" } }
            },
            new AdapterAction
            {
                Name = "get_friend_list", Label = "获取好友列表", Description = "返回好友列表",
                Params = new()
            },
            new AdapterAction
            {
                Name = "recall", Label = "撤回消息", Description = "撤回指定消息",
                Params = new() { new ActionParam { Name = "message_id", Label = "消息ID", Type = "text" } }
            },
            new AdapterAction
            {
                Name = "poke", Label = "戳一戳", Description = "向指定用户发送戳一戳",
                Params = new()
                {
                    new ActionParam { Name = "user_id", Label = "用户QQ", Type = "text" },
                    new ActionParam { Name = "group_id", Label = "群号（可选）", Type = "text", Required = false }
                }
            },
            new AdapterAction
            {
                Name = "set_group_card", Label = "设置群名片", Description = "修改群成员名片",
                Params = new()
                {
                    new ActionParam { Name = "group_id", Label = "群号", Type = "text" },
                    new ActionParam { Name = "user_id", Label = "用户QQ", Type = "text" },
                    new ActionParam { Name = "card", Label = "新名片", Type = "text" }
                }
            },
        };

        public async Task<ActionResult> ExecuteActionAsync(string action, Dictionary<string, string> parameters)
        {
            try
            {
                switch (action)
                {
                    case "get_group_list":
                        var groups = await actions.GetGroupListAsync();
                        return new ActionResult { Success = groups != null, Result = groups?.ToString() };

                    case "get_group_member_list":
                        if (!parameters.TryGetValue("group_id", out var gid) || !long.TryParse(gid, out var groupId))
                            return new ActionResult { Success = false, Error = "缺少 group_id 参数" };
                        var members = await actions.GetGroupMemberListAsync(groupId);
                        return new ActionResult { Success = members != null, Result = members?.ToString() };

                    case "get_friend_list":
                        var friends = await actions.GetFriendListAsync();
                        return new ActionResult { Success = friends != null, Result = friends?.ToString() };

                    case "recall":
                        if (!parameters.TryGetValue("message_id", out var mid) || !long.TryParse(mid, out var msgId))
                            return new ActionResult { Success = false, Error = "缺少 message_id 参数" };
                        var recalled = await actions.RecallMessageAsync(msgId);
                        return new ActionResult { Success = recalled };

                    case "poke":
                        if (!parameters.TryGetValue("user_id", out var uid) || !long.TryParse(uid, out var pokeUserId))
                            return new ActionResult { Success = false, Error = "缺少 user_id 参数" };
                        long? pokeGroupId = null;
                        if (parameters.TryGetValue("group_id", out var pgid) && long.TryParse(pgid, out var pg))
                            pokeGroupId = pg;
                        var poked = await actions.SendPokeAsync(pokeUserId, pokeGroupId);
                        return new ActionResult { Success = poked };

                    case "set_group_card":
                        if (!parameters.TryGetValue("group_id", out var cgid) || !long.TryParse(cgid, out var cardGroupId))
                            return new ActionResult { Success = false, Error = "缺少 group_id 参数" };
                        if (!parameters.TryGetValue("user_id", out var cuid) || !long.TryParse(cuid, out var cardUserId))
                            return new ActionResult { Success = false, Error = "缺少 user_id 参数" };
                        var card = parameters.GetValueOrDefault("card", "");
                        var cardSet = await actions.SetGroupCardAsync(cardGroupId, cardUserId, card);
                        return new ActionResult { Success = cardSet };

                    default:
                        return new ActionResult { Success = false, Error = $"未知操作: {action}" };
                }
            }
            catch (Exception ex)
            {
                return new ActionResult { Success = false, Error = ex.Message };
            }
        }
    }
}
