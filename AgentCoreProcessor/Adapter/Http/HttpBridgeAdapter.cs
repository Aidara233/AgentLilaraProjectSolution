using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// HTTP + WebSocket 混合协议适配器。独立端口监听（自建 Kestrel），提供：
    /// - POST /api/send：发消息，wait=true 阻塞到 bot 本次突发（burst）结束统一返回回复
    /// - GET /api/channels、/api/messages：频道列表 + 聊天记录查询（afterId 增量 / beforeId / keyword）
    /// - GET /api/status：适配器 + 引擎状态
    /// - WS /ws：双向实时，订阅频道收 bot 出向推送、发消息
    /// 定位：测试调试 + 通用外部接入。频道模型 = 默认单频道（defaultChannel 一条连续对话线）+ 可选 channel 分区。
    /// </summary>
    internal class HttpBridgeAdapter : IAdapter, IEngineAwareAdapter, IChannelAwareAdapter
    {
        public string Id { get; }
        public string Platform => "http";
        public event Action<IncomingMessage>? OnMessageReceived;

        private readonly HttpBridgeConfig config;
        private MasterEngine? engine;

        private WebApplication? app;
        private CancellationTokenSource? cts;

        // WS 订阅：channel → 客户端集合；allClients 用于统一清理
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WsClient>> subscriptions = new();
        private readonly ConcurrentDictionary<string, WsClient> allClients = new();

        private long outSeq;

        private AdapterConnectionState state = AdapterConnectionState.Stopped;
        private DateTime? startedAt, lastMessageSentAt, lastMessageReceivedAt;
        private long messagesSent, messagesReceived;
        private string? lastError;
        private DateTime? lastErrorAt;

        public HttpBridgeAdapter(string id, HttpBridgeConfig config)
        {
            Id = id;
            this.config = config;
        }

        public bool OwnsChannel(string channelId)
            => !string.IsNullOrEmpty(channelId)
               && ((!string.IsNullOrEmpty(config.ChannelPrefix) && channelId.StartsWith(config.ChannelPrefix))
                   || config.Channels.Contains(channelId));

        // ── 生命周期：自建 Kestrel（非阻塞托管） ──

        public async Task StartAsync(CancellationToken ct = default)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Args 隔离主进程命令行参数（--urls 等），避免端口错乱；ContentRootPath 固定到程序目录
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Environment.EnvironmentName = "Production";
            builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
            builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}");
            builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(5));

            var app = builder.Build();
            this.app = app;

            if (!string.IsNullOrEmpty(config.Token))
                app.Use(RequireTokenAsync);
            app.UseWebSockets();

            app.MapPost("/api/send", HandleSendAsync);
            app.MapGet("/api/channels", HandleChannelsAsync);
            app.MapGet("/api/messages", HandleMessagesAsync);
            app.MapGet("/api/status", HandleStatusAsync);
            app.Map("/ws", HandleWebSocketAsync);

            state = AdapterConnectionState.Connecting;
            try
            {
                await app.StartAsync(ct); // 非阻塞：返回即监听成功，绝不 app.Run()
                state = AdapterConnectionState.Connected;
                startedAt = DateTime.Now;
                Signal.Event(LogGroup.Adapter, "HTTP桥接适配器已启动",
                    new { id = Id, port = config.Port, defaultChannel = config.DefaultChannel, ws = "/ws", token = !string.IsNullOrEmpty(config.Token) });
            }
            catch (Exception ex)
            {
                state = AdapterConnectionState.Error;
                lastError = ex.Message;
                lastErrorAt = DateTime.Now;
                Signal.Error(LogGroup.Adapter, "HTTP桥接适配器启动失败", new { id = Id, port = config.Port, error = ex.Message });
                try { await app.DisposeAsync(); } catch { }
                this.app = null;
                // 吞掉异常：不让 StartAllAsync 的 Task.WhenAll 炸掉整条启动链
            }
        }

        public async Task StopAsync()
        {
            cts?.Cancel();
            foreach (var c in allClients.Values.ToList())
                await c.CloseAsync();
            allClients.Clear();
            subscriptions.Clear();
            if (app != null)
            {
                try
                {
                    await app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await app.DisposeAsync().ConfigureAwait(false);
                }
                catch { }
                app = null;
            }
            state = AdapterConnectionState.Stopped;
        }

        public AdapterStatus GetStatus() => new()
        {
            Id = Id,
            Platform = Platform,
            Enabled = state != AdapterConnectionState.Stopped,
            State = state,
            StartedAt = startedAt,
            LastMessageSentAt = lastMessageSentAt,
            LastMessageReceivedAt = lastMessageReceivedAt,
            MessagesSent = Interlocked.Read(ref messagesSent),
            MessagesReceived = Interlocked.Read(ref messagesReceived),
            LastError = lastError,
            LastErrorAt = lastErrorAt
        };

        public void AttachEngine(MasterEngine engine) => this.engine = engine;

        // ── 出向中枢：模型 speak → 此方法。WS 推送 + burst 收集 + 返回非 null id ──

        public Task<string?> SendMessageAsync(OutgoingMessage message)
        {
            var seq = Interlocked.Increment(ref outSeq);
            var snap = new OutgoingSnapshot
            {
                Channel = message.ChannelId,
                Content = message.Content,
                Segments = message.Segments,
                ReplyTo = message.ReplyTo,
                Mentions = message.Mentions,
                Seq = seq,
                Time = DateTime.Now
            };

            PushToChannel(message.ChannelId, snap);

            Interlocked.Increment(ref messagesSent);
            lastMessageSentAt = DateTime.Now;

            // 必须返回非 null，否则 speak 按 sentIds.Count==0 误判失败触发重试（FileAdapter 同款坑）
            return Task.FromResult<string?>($"http_{seq:D8}");
        }

        private void PushToChannel(string channel, OutgoingSnapshot snap)
        {
            if (!subscriptions.TryGetValue(channel, out var clients) || clients.IsEmpty) return;
            var json = JsonConvert.SerializeObject(new JObject
            {
                ["type"] = "out",
                ["channel"] = channel,
                ["content"] = snap.Content,
                ["seq"] = snap.Seq,
                ["time"] = snap.Time.ToString("O")
            });
            foreach (var c in clients.Values.ToList())
                _ = Task.Run(async () => { try { await c.SendAsync(json, CancellationToken.None); } catch { } });
        }

        // ── 鉴权中间件（HTTP 头/查询 + WS 握手共用） ──

        private async Task RequireTokenAsync(HttpContext ctx, RequestDelegate next)
        {
            var token = config.Token;
            if (string.IsNullOrEmpty(token)) { await next(ctx); return; }
            var authz = ctx.Request.Headers.Authorization.ToString();
            var provided = authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authz["Bearer ".Length..]
                : ctx.Request.Headers["X-Api-Token"].ToString();
            if (string.IsNullOrEmpty(provided)) provided = ctx.Request.Query["token"].ToString();

            var ok = !string.IsNullOrEmpty(provided) && provided.Length == token.Length
                     && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(token));
            if (!ok)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("{\"ok\":false,\"error\":\"unauthorized\"}");
                return;
            }
            await next(ctx);
        }

        // ── HTTP 端点 ──

        private async Task HandleSendAsync(HttpContext ctx)
        {
            try
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                var req = ParseSendRequest(body);
                if (string.IsNullOrWhiteSpace(req.Content))
                    await WriteJson(ctx, new JObject { ["ok"] = false, ["error"] = "content required" });

                var channel = string.IsNullOrWhiteSpace(req.Channel) ? config.DefaultChannel : req.Channel;
                var msg = new IncomingMessage
                {
                    Platform = Platform,
                    PlatformUserId = string.IsNullOrWhiteSpace(req.User) ? config.DefaultUser : req.User,
                    DisplayName = req.DisplayName,
                    ChannelId = channel,
                    Content = req.Content,
                    IsPrivate = req.IsPrivate ?? config.DefaultIsPrivate,
                    IsMentioned = req.IsMentioned ?? false,
                    ReplyTo = req.ReplyTo,
                    Time = DateTime.Now
                };

                var engineRef = engine;
                var afterId = 0L;
                if (engineRef?.Session != null)
                {
                    // 发送前记录频道基线（最大已存在消息 id），作为本轮 burst 的起始游标
                    var chBefore = await engineRef.Session.FindChannelByNameAsync(channel);
                    if (chBefore != null)
                    {
                        var latest = await engineRef.Session.GetContextByChannelAsync(chBefore.Id, 1);
                        afterId = latest.Count > 0 ? latest[0].Id : 0;
                    }
                }

                OnMessageReceived?.Invoke(msg);
                Interlocked.Increment(ref messagesReceived);
                lastMessageReceivedAt = DateTime.Now;

                if (!req.Wait || engineRef == null || engineRef.Session == null)
                {
                    await WriteJson(ctx, new JObject { ["ok"] = true, ["accepted"] = true });
                    return;
                }

                // 基于入库消息轮询：bot 回复必然入库（SaveBotMessageAsync），比 HasBusyWorker 时序更可靠。
                // 发送后轮询该频道 afterId 之后的新消息，收集 IsFromBot；结束 = 引擎空闲 + 连续 N 次无新消息。
                var timeoutMs = req.TimeoutMs ?? config.BurstTimeoutMs;
                var observe = TimeSpan.FromMilliseconds(Math.Min(config.BusyObserveMs, timeoutMs));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var deadline = TimeSpan.FromMilliseconds(timeoutMs);
                bool seen = false;
                bool busyEver = false;
                int settle = 0;
                var botReplies = new List<UserMessage>();
                long currentAfterId = afterId;

                while (sw.Elapsed < deadline)
                {
                    var ch = await engineRef.Session.FindChannelByNameAsync(channel);
                    if (ch != null)
                    {
                        var newMsgs = await engineRef.Session.GetMessagesAfterIdAsync(ch.Id, (int)currentAfterId, 50);
                        if (newMsgs.Count > 0)
                        {
                            seen = true;
                            foreach (var m in newMsgs)
                            {
                                currentAfterId = Math.Max(currentAfterId, m.Id);
                                if (m.IsFromBot) botReplies.Add(m);
                            }
                            settle = 0;
                        }
                        else if (seen)
                        {
                            if (engineRef.HasBusyWorker()) { busyEver = true; settle = 0; }
                            else if (busyEver && ++settle >= config.SettleChecks) break; // burst 已发生并结束
                            // seen 但引擎从未 busy：消息已入库，bot 可能延迟响应（express 会话激活可达数秒），
                            // 不提前 break，继续等待直至超时。只有从未 seen 才走 observe 快速返回。
                        }
                        else if (sw.Elapsed > observe)
                        {
                            break; // 观察窗内消息未入库（命令/被拦截），快速返回
                        }
                    }
                    await Task.Delay(config.SettleIntervalMs);
                }

                bool timedOut = sw.Elapsed >= deadline;

                var arr = new JArray();
                foreach (var r in botReplies)
                    arr.Add(new JObject
                    {
                        ["id"] = r.Id,
                        ["content"] = r.Content,
                        ["senderName"] = r.SenderName,
                        ["time"] = r.Time.ToString("O")
                    });

                await WriteJson(ctx, new JObject
                {
                    ["ok"] = true,
                    ["burstSeen"] = seen,
                    ["timedOut"] = timedOut,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["replies"] = arr
                });
            }
            catch (Exception ex)
            {
                await WriteJson(ctx, new JObject { ["ok"] = false, ["error"] = ex.Message });
            }
        }

        private async Task HandleChannelsAsync(HttpContext ctx)
        {
            try
            {
                var engineRef = engine;
                if (engineRef == null || engineRef.Session == null)
                {
                    await WriteJson(ctx, new JObject { ["ok"] = false, ["error"] = "engine-not-ready" });
                    return;
                }
                var channels = await engineRef.Session.GetAllChannelsAsync();
                var arr = new JArray();
                foreach (var c in channels)
                    arr.Add(new JObject { ["id"] = c.Id, ["name"] = c.Name });
                await WriteJson(ctx, new JObject { ["ok"] = true, ["channels"] = arr });
            }
            catch (Exception ex) { await WriteJson(ctx, new JObject { ["ok"] = false, ["error"] = ex.Message }); }
        }

        private async Task HandleMessagesAsync(HttpContext ctx)
        {
            try
            {
                var engineRef = engine;
                if (engineRef == null || engineRef.Session == null)
                {
                    await WriteJson(ctx, new JObject { ["ok"] = false, ["error"] = "engine-not-ready" });
                    return;
                }

                var q = ctx.Request.Query;
                var channelName = q["channel"].ToString();
                if (string.IsNullOrEmpty(channelName)) channelName = config.DefaultChannel;
                var channel = await engineRef.Session.FindChannelByNameAsync(channelName);
                if (channel == null)
                {
                    await WriteJson(ctx, new JObject { ["ok"] = true, ["channel"] = null, ["messages"] = new JArray() });
                    return;
                }

                var limit = int.TryParse(q["limit"], out var lim) ? Math.Clamp(lim, 1, 200) : 50;
                var keyword = q["keyword"].ToString();
                List<UserMessage> messages;
                if (!string.IsNullOrEmpty(keyword))
                {
                    var offset = int.TryParse(q["offset"], out var off) ? Math.Max(0, off) : 0;
                    messages = (await engineRef.Session.SearchMessagesByChannelAsync(channel.Id, keyword, offset, limit))
                        .OrderBy(m => m.Id).ToList();
                }
                else if (int.TryParse(q["afterId"], out var aid) && aid > 0)
                    messages = await engineRef.Session.GetMessagesAfterIdAsync(channel.Id, aid, limit); // 已升序
                else if (int.TryParse(q["beforeId"], out var bid) && bid > 0)
                    messages = (await engineRef.Session.GetMessagesBeforeIdAsync(channel.Id, bid, limit))
                        .OrderBy(m => m.Id).ToList();
                else
                    messages = (await engineRef.Session.GetContextByChannelAsync(channel.Id, limit))
                        .OrderBy(m => m.Id).ToList();

                var arr = new JArray();
                foreach (var m in messages)
                    arr.Add(new JObject
                    {
                        ["id"] = m.Id,
                        ["userId"] = m.UserId,
                        ["senderName"] = m.SenderName,
                        ["content"] = m.Content,
                        ["isFromBot"] = m.IsFromBot,
                        ["time"] = m.Time.ToString("O"),
                        ["platformMessageId"] = m.PlatformMessageId
                    });
                await WriteJson(ctx, new JObject
                {
                    ["ok"] = true,
                    ["channel"] = new JObject { ["id"] = channel.Id, ["name"] = channel.Name },
                    ["messages"] = arr
                });
            }
            catch (Exception ex) { await WriteJson(ctx, new JObject { ["ok"] = false, ["error"] = ex.Message }); }
        }

        private Task HandleStatusAsync(HttpContext ctx)
        {
            var engineRef = engine;
            return WriteJson(ctx, new JObject
            {
                ["ok"] = true,
                ["adapter"] = new JObject
                {
                    ["id"] = Id,
                    ["platform"] = Platform,
                    ["state"] = state.ToString(),
                    ["port"] = config.Port,
                    ["defaultChannel"] = config.DefaultChannel,
                    ["messagesSent"] = Interlocked.Read(ref messagesSent),
                    ["messagesReceived"] = Interlocked.Read(ref messagesReceived),
                    ["startedAt"] = startedAt?.ToString("O"),
                    ["lastError"] = lastError
                },
                ["engine"] = engineRef == null ? null : new JObject
                {
                    ["isIdle"] = engineRef.IsIdle,
                    ["busyWorker"] = engineRef.HasBusyWorker()
                }
            });
        }

        // ── WebSocket 端点 ──

        private async Task HandleWebSocketAsync(HttpContext ctx)
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            WebSocket socket;
            try { socket = await ctx.WebSockets.AcceptWebSocketAsync(); }
            catch { return; }

            var client = new WsClient(socket);
            allClients[client.Id] = client;

            var initial = ctx.Request.Query["channel"].ToString();
            var subscribed = new List<string>();
            if (!string.IsNullOrEmpty(initial))
            {
                Subscribe(client, initial);
                subscribed.Add(initial);
            }
            await client.SendAsync(JsonConvert.SerializeObject(new JObject
            {
                ["type"] = "welcome",
                ["clientId"] = client.Id,
                ["subscribed"] = JArray.FromObject(subscribed)
            }), CancellationToken.None);

            try
            {
                var buffer = new byte[8192];
                while (socket.State == WebSocketState.Open && !(cts?.IsCancellationRequested ?? false))
                {
                    WebSocketReceiveResult result;
                    using var ms = new MemoryStream();
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
                            return;
                        }
                        if (result.Count > 0) ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    _ = HandleWsFrameAsync(client, text);
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }
            finally
            {
                UnsubscribeAll(client);
                allClients.TryRemove(client.Id, out _);
            }
        }

        private async Task HandleWsFrameAsync(WsClient client, string text)
        {
            try
            {
                var json = JObject.Parse(text);
                switch (json["type"]?.ToString() ?? "")
                {
                    case "subscribe":
                        var subCh = json["channel"]?.ToString();
                        if (string.IsNullOrEmpty(subCh)) { await client.SendAsync(ErrJson("missing-channel"), CancellationToken.None); return; }
                        Subscribe(client, subCh);
                        await client.SendAsync(AckJson("subscribe", subCh), CancellationToken.None);
                        break;
                    case "unsubscribe":
                        var unsubCh = json["channel"]?.ToString() ?? "";
                        Unsubscribe(client, unsubCh);
                        await client.SendAsync(AckJson("unsubscribe", unsubCh), CancellationToken.None);
                        break;
                    case "send":
                        await HandleWsSendAsync(client, json);
                        break;
                    case "ping":
                        await client.SendAsync("{\"type\":\"pong\"}", CancellationToken.None);
                        break;
                    default:
                        await client.SendAsync(ErrJson("unknown-type"), CancellationToken.None);
                        break;
                }
            }
            catch (Exception ex)
            {
                try { await client.SendAsync(ErrJson(ex.Message), CancellationToken.None); } catch { }
            }
        }

        private async Task HandleWsSendAsync(WsClient client, JObject json)
        {
            var channel = json["channel"]?.ToString();
            if (string.IsNullOrEmpty(channel)) { await client.SendAsync(ErrJson("missing-channel"), CancellationToken.None); return; }
            var content = json["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(content)) { await client.SendAsync(ErrJson("content required"), CancellationToken.None); return; }

            var msg = new IncomingMessage
            {
                Platform = Platform,
                PlatformUserId = json["user"]?.ToString() ?? config.DefaultUser,
                DisplayName = json["displayName"]?.ToString(),
                ChannelId = channel,
                Content = content,
                IsPrivate = json["isPrivate"]?.Value<bool?>() ?? config.DefaultIsPrivate,
                IsMentioned = json["isMentioned"]?.Value<bool?>() ?? false,
                ReplyTo = json["replyTo"]?.ToString(),
                Time = DateTime.Now
            };
            OnMessageReceived?.Invoke(msg);
            Interlocked.Increment(ref messagesReceived);
            lastMessageReceivedAt = DateTime.Now;
            await client.SendAsync(AckJson("send", channel), CancellationToken.None);
        }

        private void Subscribe(WsClient client, string channel)
        {
            if (string.IsNullOrEmpty(channel)) return;
            var clients = subscriptions.GetOrAdd(channel, _ => new ConcurrentDictionary<string, WsClient>());
            clients[client.Id] = client;
            client.Channels[channel] = 1;
        }

        private void Unsubscribe(WsClient client, string channel)
        {
            if (string.IsNullOrEmpty(channel)) return;
            if (subscriptions.TryGetValue(channel, out var clients))
                clients.TryRemove(client.Id, out _);
            client.Channels.TryRemove(channel, out _);
        }

        private void UnsubscribeAll(WsClient client)
        {
            foreach (var ch in client.Channels.Keys.ToList())
                Unsubscribe(client, ch);
        }

        // ── 辅助 ──

        private static SendRequest ParseSendRequest(string body)
        {
            var json = JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            return new SendRequest
            {
                Channel = json["channel"]?.ToString() ?? "",
                Content = json["content"]?.ToString() ?? "",
                User = json["user"]?.ToString(),
                DisplayName = json["displayName"]?.ToString(),
                IsPrivate = json["isPrivate"]?.Value<bool?>(),
                IsMentioned = json["isMentioned"]?.Value<bool?>(),
                ReplyTo = json["replyTo"]?.ToString(),
                Wait = json["wait"]?.Value<bool?>() ?? true,
                TimeoutMs = json["timeoutMs"]?.Value<int?>()
            };
        }

        private static Task WriteJson(HttpContext ctx, JObject obj)
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync(obj.ToString(Formatting.None));
        }

        private static string ErrJson(string message) =>
            JsonConvert.SerializeObject(new JObject { ["type"] = "error", ["message"] = message });

        private static string AckJson(string op, string channel) =>
            JsonConvert.SerializeObject(new JObject { ["type"] = "ack", ["op"] = op, ["channel"] = channel });

        // ── 嵌套类型 ──

        private sealed class WsClient
        {
            public string Id { get; } = Guid.NewGuid().ToString("N");
            public WebSocket Socket { get; }
            public ConcurrentDictionary<string, byte> Channels { get; } = new();
            private readonly SemaphoreSlim sendLock = new(1, 1);

            public WsClient(WebSocket socket) => Socket = socket;

            public async Task SendAsync(string json, CancellationToken ct)
            {
                if (Socket.State != WebSocketState.Open) return;
                var bytes = Encoding.UTF8.GetBytes(json);
                await sendLock.WaitAsync(ct);
                try
                {
                    if (Socket.State != WebSocketState.Open) return;
                    await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                }
                catch { }
                finally { sendLock.Release(); }
            }

            public async Task CloseAsync()
            {
                try
                {
                    if (Socket.State == WebSocketState.Open)
                        await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
                    Socket.Dispose();
                }
                catch { }
            }
        }
    }
}
