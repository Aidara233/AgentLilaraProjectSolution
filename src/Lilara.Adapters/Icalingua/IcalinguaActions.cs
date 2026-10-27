using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// Icalingua 出站：构造 SendMessageParams → sendMessage RPC / HTTP 图片上传（§6, §8.4）。
    /// </summary>
    internal class IcalinguaActions
    {
        private readonly IcalinguaAdapter adapter;

        public IcalinguaActions(IcalinguaAdapter adapter)
        {
            this.adapter = adapter;
        }

        public async Task<string?> SendMessageAsync(OutgoingMessage message)
        {
            var roomId = ParseRoomId(message.ChannelId);
            if (roomId == 0) return null;

            var sb = new StringBuilder();
            var media = new List<JObject>();
            JObject? replyMessage = null;

            // 回复引用：replyMessage._id = 被引用消息 id
            if (!string.IsNullOrEmpty(message.ReplyTo))
                replyMessage = new JObject { ["_id"] = message.ReplyTo };

            if (message.Segments is { Count: > 0 })
            {
                // 新路径：有序 segments（文本/图片/@ 交错）
                foreach (var seg in message.Segments)
                {
                    switch (seg.Type)
                    {
                        case SegmentType.Text:
                            if (!string.IsNullOrEmpty(seg.Text)) sb.Append(seg.Text);
                            break;
                        case SegmentType.At:
                            var qq = seg.AtPlatformId;
                            if (qq == "all") qq = "1";
                            sb.Append($"<IcalinguaAt qq={qq}></IcalinguaAt>");
                            break;
                        case SegmentType.Image:
                            var b64 = await ReadImageB64Async(seg.ImagePath);
                            if (b64 != null) media.Add(new JObject { ["b64"] = b64, ["type"] = "image/jpeg" });
                            break;
                        case SegmentType.Reply:
                            // 回复已在消息级 replyMessage 处理
                            break;
                    }
                }
            }
            else
            {
                // 旧路径：Content + Mentions + Attachments（兼容非 Segments 调用方）
                var content = message.Content ?? "";
                var atDelim = InlineMentionProtocol.Delimiter;
                var atPrefix = InlineMentionProtocol.Prefix;

                if (content.Contains(atDelim))
                {
                    foreach (var part in content.Split(atDelim))
                    {
                        if (part.StartsWith(atPrefix))
                        {
                            var qq = part[atPrefix.Length..];
                            if (qq == "all") qq = "1";
                            sb.Append($"<IcalinguaAt qq={qq}></IcalinguaAt>");
                        }
                        else if (part.Length > 0)
                        {
                            sb.Append(part);
                        }
                    }
                }
                else
                {
                    if (message.Mentions != null)
                    {
                        foreach (var qq in message.Mentions)
                        {
                            var q = qq == "all" ? "1" : qq;
                            sb.Append($"<IcalinguaAt qq={q}></IcalinguaAt>");
                        }
                    }
                    var textContent = message.Mentions is { Count: > 0 } && !content.StartsWith(" ")
                        ? " " + content
                        : content;
                    sb.Append(textContent);
                }

                // 附件（追加 media）
                if (message.Attachments is { Count: > 0 })
                {
                    foreach (var att in message.Attachments)
                    {
                        if (att.Type == AttachmentType.Image)
                        {
                            var b64 = await ReadImageB64Async(att.LocalPath ?? att.SourceUrl);
                            if (b64 != null) media.Add(new JObject { ["b64"] = b64, ["type"] = "image/jpeg" });
                        }
                        else if (att.Type == AttachmentType.Audio)
                        {
                            var a64 = await ReadImageB64Async(att.LocalPath ?? att.SourceUrl);
                            if (a64 != null) media.Add(new JObject { ["b64"] = a64, ["type"] = "audio/ogg" });
                        }
                        // File 类型：icalingua 文件上传需分片/群文件，首版不支持，忽略
                    }
                }
            }

            var contentStr = sb.ToString();
            if (string.IsNullOrEmpty(contentStr) && media.Count == 0)
                return null;

            var sendParams = new JObject
            {
                ["content"] = contentStr,
                ["roomId"] = roomId,
                ["messageType"] = "text",
                ["at"] = new JArray()
            };
            if (replyMessage != null) sendParams["replyMessage"] = replyMessage;
            if (media.Count > 0) sendParams["media"] = new JArray(media);

            // 带 b64 媒体（图片/语音）→ 走 HTTP /api/{token}/sendMessage（§8.4）
            if (media.Count > 0)
                return await SendViaHttpAsync(sendParams);

            // 纯文本/@/url 图片 → sendMessage RPC
            await adapter.EmitWithAckAsync(IcalinguaProtocol.SendMessage, new object[] { sendParams });
            adapter.TrackSent();
            return "0"; // sendMessage 无 message_id 回执，用 "0" 哨兵表示发出
        }

        // ── 发送路径 ──

        private async Task<string?> SendViaHttpAsync(JObject sendParams)
        {
            try
            {
                // 1. 拿一次性 sendImg token
                var token = await adapter.RpcStringAsync(IcalinguaProtocol.RequestToken);
                if (string.IsNullOrEmpty(token))
                {
                    Signal.Warn(LogGroup.Adapter, "Icalingua requestToken 失败", new { id = adapter.Id });
                    return null;
                }

                // 2. POST /api/{token}/sendMessage
                var baseUrl = adapter.Config.Server.TrimEnd('/');
                var url = string.Format(IcalinguaProtocol.HttpSendMessageUri, token);
                var full = baseUrl + url;

                var json = sendParams.ToString(Formatting.None);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await adapter.HttpClient.PostAsync(full, content);
                if ((int)resp.StatusCode == 202 || resp.IsSuccessStatusCode)
                {
                    adapter.TrackSent();
                    return "0";
                }
                Signal.Warn(LogGroup.Adapter, "Icalingua HTTP发送失败", new { id = adapter.Id, status = (int)resp.StatusCode });
                adapter.TrackError($"HTTP {resp.StatusCode}");
                return null;
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Adapter, "Icalingua HTTP发送异常", new { id = adapter.Id, error = ex.Message });
                adapter.TrackError(ex.Message);
                return null;
            }
        }

        // ── 辅助 ──

        private static async Task<string?> ReadImageB64Async(string? pathOrUrl)
        {
            if (string.IsNullOrEmpty(pathOrUrl)) return null;
            string path = pathOrUrl;
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                path = new Uri(path).LocalPath;
            if (File.Exists(path))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path);
                    return Convert.ToBase64String(bytes);
                }
                catch { return null; }
            }
            // 远程 url：尝试下载再编码
            return null;
        }

        private static long ParseRoomId(string channelId)
        {
            if (channelId.StartsWith("group_"))
                return long.TryParse(channelId.Substring("group_".Length), out var g) ? -g : 0;
            if (channelId.StartsWith("private_"))
                return long.TryParse(channelId.Substring("private_".Length), out var u) ? u : 0;
            return 0;
        }
    }
}
