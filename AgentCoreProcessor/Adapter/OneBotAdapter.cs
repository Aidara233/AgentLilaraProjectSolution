using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Adapter
{
    public class OneBotConfig
    {
        public string WsUrl { get; set; } = "ws://localhost:3001";
        public string Token { get; set; } = "";
        public string FilterMode { get; set; } = "whitelist";
        public List<string> Whitelist { get; set; } = new();
        public List<string> Blacklist { get; set; } = new();
        public List<string> BotNames { get; set; } = new();
    }

    public class OneBotAdapter : IAdapter
    {
        public string Platform => "qq";
        public event Action<IncomingMessage>? OnMessageReceived;

        private readonly string configPath;
        private OneBotConfig config = new();
        private ClientWebSocket? ws;
        private CancellationTokenSource? cts;
        private long selfId;

        // 图片下载用
        private readonly HttpClient httpClient = new();

        // API 请求-响应关联
        private int echoCounter;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> pendingCalls = new();

        // bot 发送消息的 message_id 集合（用于判断 reply 是否引用了 bot）
        private readonly HashSet<long> sentMessageIds = new();
        private const int MaxSentMessageIds = 200;

        // 重连退避
        private const int MaxReconnectDelayMs = 30000;

        public OneBotAdapter(string configPath)
        {
            this.configPath = configPath;
        }

        // 接收循环任务，可供外部 await 以保持进程活跃
        private Task? receiveTask;

        public async Task StartAsync(CancellationToken ct = default)
        {
            // 加载配置
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                config = JsonConvert.DeserializeObject<OneBotConfig>(json) ?? new OneBotConfig();
            }
            else
            {
                FrameworkLogger.Log("OneBotAdapter", $"配置文件不存在: {configPath}，使用默认配置");
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // 首次连接
            await ConnectAsync(cts.Token);

            // 先启动接收循环（后台），再查询 selfId（需要接收循环读取响应）
            receiveTask = RunReceiveLoopWithReconnectAsync(cts.Token);

            selfId = await GetSelfIdAsync();
            FrameworkLogger.Log("OneBotAdapter", $"已连接，selfId={selfId}");
        }

        /// <summary>等待适配器运行结束（用于保持进程活跃）。</summary>
        public Task WaitAsync() => receiveTask ?? Task.CompletedTask;

        public async Task StopAsync()
        {
            cts?.Cancel();

            // 完成所有等待中的 API 调用
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
            httpClient?.Dispose();
            ws = null;

            FrameworkLogger.Log("OneBotAdapter", "已停止");
        }

        public Task ReloadConfigAsync()
        {
            if (!File.Exists(configPath))
            {
                FrameworkLogger.Log("OneBotAdapter", $"配置文件不存在: {configPath}，跳过重载");
                return Task.CompletedTask;
            }

            var json = File.ReadAllText(configPath);
            var newConfig = JsonConvert.DeserializeObject<OneBotConfig>(json) ?? new OneBotConfig();

            // 检查连接参数是否变化
            bool connectionChanged = newConfig.WsUrl != config.WsUrl || newConfig.Token != config.Token;

            config = newConfig;
            FrameworkLogger.Log("OneBotAdapter",
                $"配置已重载: filterMode={config.FilterMode}, whitelist=[{string.Join(",", config.Whitelist)}]");

            if (connectionChanged)
                FrameworkLogger.Log("OneBotAdapter", "WsUrl 或 Token 已变更，需要重启适配器才能生效");

            return Task.CompletedTask;
        }

        public async Task<string?> SendMessageAsync(OutgoingMessage message)
        {
            string action;
            var p = new JObject();

            if (message.ChannelId.StartsWith("group_"))
            {
                action = "send_group_msg";
                p["group_id"] = long.Parse(message.ChannelId[6..]);
            }
            else if (message.ChannelId.StartsWith("private_"))
            {
                action = "send_private_msg";
                p["user_id"] = long.Parse(message.ChannelId[8..]);
            }
            else
            {
                FrameworkLogger.Log("OneBotAdapter", $"无法识别的 ChannelId 格式: {message.ChannelId}");
                return null;
            }

            // 构造消息段
            var segments = new JArray();

            // reply 段（引用消息）
            if (!string.IsNullOrEmpty(message.ReplyTo))
            {
                segments.Add(new JObject
                {
                    ["type"] = "reply",
                    ["data"] = new JObject { ["id"] = message.ReplyTo }
                });
            }

            // at 段
            if (message.Mentions != null)
            {
                foreach (var qq in message.Mentions)
                {
                    segments.Add(new JObject
                    {
                        ["type"] = "at",
                        ["data"] = new JObject { ["qq"] = qq }
                    });
                }
            }

            // text 段（at 段后补空格，防止文字紧贴 @名字）
            var textContent = message.Mentions is { Count: > 0 } && !message.Content.StartsWith(" ")
                ? " " + message.Content
                : message.Content;
            segments.Add(new JObject
            {
                ["type"] = "text",
                ["data"] = new JObject { ["text"] = textContent }
            });
            p["message"] = segments;

            var resp = await CallApiAsync(action, p);
            if (resp != null)
            {
                if (resp["retcode"]?.Value<int>() != 0)
                {
                    FrameworkLogger.Log("OneBotAdapter",
                        $"发送失败: action={action}, retcode={resp["retcode"]}, msg={resp["message"]}");
                }
                else
                {
                    var sentId = resp["data"]?["message_id"]?.Value<long>() ?? 0;
                    if (sentId != 0)
                    {
                        lock (sentMessageIds)
                        {
                            if (sentMessageIds.Count >= MaxSentMessageIds)
                                sentMessageIds.Clear();
                            sentMessageIds.Add(sentId);
                        }
                        return sentId.ToString();
                    }
                }
            }
            return null;
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
                    FrameworkLogger.Log("OneBotAdapter", $"连接断开: {ex.Message}");
                }

                if (ct.IsCancellationRequested) break;

                // 重连
                FrameworkLogger.Log("OneBotAdapter", $"将在 {reconnectDelayMs}ms 后重连...");
                try
                {
                    await Task.Delay(reconnectDelayMs, ct);
                }
                catch (OperationCanceledException) { break; }

                try
                {
                    await ConnectAsync(ct);
                    selfId = await GetSelfIdAsync();
                    FrameworkLogger.Log("OneBotAdapter", $"重连成功，selfId={selfId}");
                    reconnectDelayMs = 1000; // 重置退避
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("OneBotAdapter", $"重连失败: {ex.Message}");
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
                        return; // 服务端关闭，触发重连
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());

                JObject data;
                try { data = JObject.Parse(json); }
                catch { continue; } // 非法 JSON，跳过

                // 区分 API 响应和事件
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

        // ── 事件处理 ──

        // 消息去重（防止同一条消息被处理两次）
        private readonly HashSet<long> recentMessageIds = new();
        private DateTime lastMessageIdCleanup = DateTime.Now;

        private async void HandleEvent(JObject data)
        {
            var postType = data["post_type"]?.ToString();
            if (postType != "message") return;

            // message_id 去重
            var messageId = data["message_id"]?.Value<long>() ?? 0;
            if (messageId != 0)
            {
                lock (recentMessageIds)
                {
                    // 定期清理（每 60 秒）
                    if ((DateTime.Now - lastMessageIdCleanup).TotalSeconds > 60)
                    {
                        recentMessageIds.Clear();
                        lastMessageIdCleanup = DateTime.Now;
                    }
                    if (!recentMessageIds.Add(messageId))
                        return; // 重复消息，跳过
                }
            }

            var msg = await ParseMessageEventAsync(data);
            if (msg != null)
                OnMessageReceived?.Invoke(msg);
        }

        private async Task<IncomingMessage?> ParseMessageEventAsync(JObject data)
        {
            var userId = data["user_id"]?.Value<long>() ?? 0;

            // 过滤自己的消息
            if (userId == selfId) return null;

            var messageType = data["message_type"]?.ToString();
            bool isPrivate = messageType == "private";

            // 构建 ChannelId
            string channelId;
            if (isPrivate)
                channelId = $"private_{userId}";
            else
                channelId = $"group_{data["group_id"]?.Value<long>() ?? 0}";

            // 黑白名单过滤
            if (!PassesFilter(channelId)) return null;

            // 解析消息段
            var segments = data["message"] as JArray;
            if (segments == null) return null;

            var textBuilder = new StringBuilder();
            bool isMentioned = false;
            string? replyTo = null;
            List<MessageAttachment>? attachments = null;
            List<string>? mentionedIds = null;

            foreach (var seg in segments)
            {
                var type = seg["type"]?.ToString();
                var segData = seg["data"] as JObject;
                if (segData == null) continue;

                switch (type)
                {
                    case "text":
                        textBuilder.Append(segData["text"]?.ToString() ?? "");
                        break;
                    case "at":
                        var atQq = segData["qq"]?.ToString();
                        if (atQq == selfId.ToString())
                            isMentioned = true;
                        if (!string.IsNullOrEmpty(atQq))
                        {
                            mentionedIds ??= new List<string>();
                            mentionedIds.Add(atQq);
                        }
                        var atName = segData["name"]?.ToString();
                        if (string.IsNullOrEmpty(atName)) atName = atQq;
                        textBuilder.Append($"@{atName} ");
                        break;
                    case "reply":
                        replyTo = segData["id"]?.ToString();
                        break;
                    case "image":
                        var imageUrl = segData["url"]?.ToString();
                        if (!string.IsNullOrEmpty(imageUrl))
                        {
                            try
                            {
                                var (localPath, imgHash) = await ImageStorage.DownloadAndSaveAsync(imageUrl, httpClient);
                                attachments ??= new List<MessageAttachment>();
                                attachments.Add(new MessageAttachment
                                {
                                    Type = AttachmentType.Image,
                                    SourceUrl = imageUrl,
                                    LocalPath = localPath,
                                    FileName = Path.GetFileName(localPath),
                                    Hash = imgHash
                                });
                            }
                            catch (Exception ex)
                            {
                                FrameworkLogger.Log("OneBotAdapter", $"图片下载失败: {ex.Message}");
                            }
                        }
                        break;
                }
            }

            var content = textBuilder.ToString().Trim();
            // 允许纯图片消息（无文本但有附件）
            if (string.IsNullOrEmpty(content) && (attachments == null || attachments.Count == 0))
                return null;

            // 文本提及检测：消息内容包含 bot 名字
            if (!isMentioned && config.BotNames.Count > 0 && !string.IsNullOrEmpty(content))
            {
                foreach (var name in config.BotNames)
                {
                    if (content.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        isMentioned = true;
                        break;
                    }
                }
            }

            // 引用检测：引用了 bot 发送的消息视为提及
            if (!isMentioned && replyTo != null && long.TryParse(replyTo, out var replyMsgId))
            {
                lock (sentMessageIds)
                {
                    if (sentMessageIds.Contains(replyMsgId))
                        isMentioned = true;
                }
            }

            // 提取发言人信息
            var sender = data["sender"] as JObject;
            var nickname = sender?["nickname"]?.ToString();
            var card = sender?["card"]?.ToString();
            // 群名片优先，昵称兜底
            var displayName = !string.IsNullOrWhiteSpace(card) ? card : nickname;

            // 拉取被引用消息的内容
            string? quotedContent = null;
            if (replyTo != null)
            {
                try
                {
                    var resp = await CallApiAsync("get_msg", new JObject { ["message_id"] = long.Parse(replyTo) });
                    if (resp?["retcode"]?.Value<int>() == 0)
                    {
                        var msgData = resp["data"];
                        var rawMsg = msgData?["raw_message"]?.ToString()
                                  ?? msgData?["message"]?.ToString();
                        if (!string.IsNullOrEmpty(rawMsg))
                            quotedContent = rawMsg.Length > 200 ? rawMsg[..200] + "..." : rawMsg;
                    }
                }
                catch { }
            }

            return new IncomingMessage
            {
                Platform = Platform,
                PlatformUserId = userId.ToString(),
                ChannelId = channelId,
                Content = content,
                DisplayName = displayName,
                Nickname = nickname,
                IsPrivate = isPrivate,
                IsMentioned = isMentioned,
                ReplyTo = replyTo,
                QuotedContent = quotedContent,
                PlatformMessageId = data["message_id"]?.ToString(),
                Time = DateTime.Now,
                Attachments = attachments,
                MentionedPlatformIds = mentionedIds
            };
        }

        private bool PassesFilter(string channelId)
        {
            return config.FilterMode.ToLower() switch
            {
                "whitelist" => config.Whitelist.Contains(channelId, StringComparer.OrdinalIgnoreCase),
                "blacklist" => !config.Blacklist.Contains(channelId, StringComparer.OrdinalIgnoreCase),
                _ => true // "none" 或其他值：不过滤
            };
        }

        // ── API 调用 ──

        private async Task<JObject?> CallApiAsync(string action, JObject? param = null)
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

                // 带超时等待响应
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(10000)).ConfigureAwait(false);
                if (completed == tcs.Task)
                    return await tcs.Task;

                // 超时
                pendingCalls.TryRemove(echo, out _);
                FrameworkLogger.Log("OneBotAdapter", $"API 调用超时: {action}");
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
    }
}
