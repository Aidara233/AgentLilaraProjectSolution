using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// Icalingua 入站解析：把 Bridge 推送的 addMessage {roomId, message} 转成项目 IncomingMessage。
    /// message.content 是 Bridge 编码的伪 Markdown mini-language（§4.1），files[] 带媒体元数据。
    /// </summary>
    internal class IcalinguaMessageParser
    {
        private readonly IcalinguaAdapter adapter;

        // 伪标记正则（§4.1）：Face / Forward / NestedForward / At / QLottie
        private static readonly Regex FaceRegex = new(@"\[Face:\s*(\d+)\]", RegexOptions.Compiled);
        private static readonly Regex ForwardRegex = new(@"\[Forward:\s*([^\r\n]+)\]", RegexOptions.Compiled);
        private static readonly Regex NestedForwardRegex = new(@"\[NestedForward:\s*([^\r\n]+)\]", RegexOptions.Compiled);
        private static readonly Regex AtRegex = new(@"<IcalinguaAt qq=([^>]+)>([^<]*)</IcalinguaAt>", RegexOptions.Compiled);
        private static readonly Regex QLottieRegex = new(@"\[QLottie:\s*([^\]]+)\]", RegexOptions.Compiled);

        public IcalinguaMessageParser(IcalinguaAdapter adapter)
        {
            this.adapter = adapter;
        }

        public async Task<IncomingMessage?> HandleAddMessage(long roomId, JObject? msg)
        {
            if (msg == null) return null;

            var (channelId, isPrivate) = IcalinguaProtocol.RoomToChannel(roomId);
            if (!PassesFilter(channelId)) return null;

            var selfId = adapter.SelfId;
            var content = msg["content"]?.ToString() ?? "";

            var textBuilder = new StringBuilder();
            var mentionedIds = new List<string>();
            bool isMentioned = false;
            string? replyTo = null;

            // ── 扫描 content 伪标记 → 文本 / @ / 媒体占位 ──
            var contentText = content;

            // @ 标记（QQ=1 表示 @全体；=selfId 表示 @bot）
            contentText = AtRegex.Replace(contentText, m =>
            {
                var qq = m.Groups[1].Value;
                var name = SafeDecode(m.Groups[2].Value);
                if (qq == "1")
                {
                    mentionedIds.Add("all");
                    return "@全体成员";
                }
                if (qq == selfId.ToString())
                    isMentioned = true;
                mentionedIds.Add(qq);
                return "@" + (string.IsNullOrEmpty(name) ? qq : name);
            });

            // 表情 → 占位文本
            contentText = FaceRegex.Replace(contentText, "[表情]");
            contentText = QLottieRegex.Replace(contentText, "[表情]");

            // 合并转发 → 占位（首版不展开嵌套树，仅提示可查）
            contentText = ForwardRegex.Replace(contentText, "[合并转发]");
            contentText = NestedForwardRegex.Replace(contentText, "[合并转发]");

            textBuilder.Append(contentText);

            // ── files[] 媒体 → attachments ──
            var attachments = new List<MessageAttachment>();
            var files = msg["files"] as JArray;
            if (files == null && msg["file"] is JObject single)
                files = new JArray(single);

            int imgCount = 0;
            if (files != null)
            {
                foreach (var f in files.OfType<JObject>())
                {
                    var type = f["type"]?.ToString() ?? "";
                    var url = f["url"]?.ToString();
                    var name = f["name"]?.ToString();
                    var isFace = f["isFace"]?.Value<bool>() ?? false;

                    if (type.StartsWith("image", StringComparison.OrdinalIgnoreCase))
                    {
                        var (att, ok) = await DownloadImageAsync(url, isFace);
                        if (att != null)
                        {
                            attachments.Add(att);
                            textBuilder.Append($"[IMG:{imgCount}]");
                            imgCount++;
                        }
                    }
                    else
                    {
                        textBuilder.Append($"[{DescribeMedia(type, name)}]");
                        attachments.Add(new MessageAttachment
                        {
                            Type = MediaType(type),
                            SourceUrl = url,
                            FileName = name
                        });
                    }
                }
            }

            // ── 引用消息（回复）──
            string? quotedContent = null;
            string? quotedSenderId = null;
            string? quotedSenderName = null;
            var replyMsg = msg["replyMessage"] as JObject;
            if (replyMsg != null)
            {
                quotedContent = replyMsg["content"]?.ToString();
                quotedSenderId = replyMsg["senderId"]?.ToString();
                quotedSenderName = replyMsg["username"]?.ToString();
                var replyId = replyMsg["_id"]?.ToString();
                if (!string.IsNullOrEmpty(replyId)) replyTo = replyId;
                // 引用到 bot 自身 → 视为被提及
                if (!isMentioned && long.TryParse(quotedSenderId, out var qid) && qid == selfId)
                    isMentioned = true;
                if (quotedContent != null && quotedContent.Length > 200)
                    quotedContent = quotedContent[..200] + "...";
            }

            // 发送者信息（防御式：尝试多个字段名）
            var senderId = FirstString(msg, "senderId", "sender_id", "user_id", "senderId");
            long userId = 0;
            if (!long.TryParse(senderId, out userId))
            {
                // 从 _id 解析发送者（§9.3：群 21 字节 / 私聊 17 字节）
                userId = ParseSenderFromId(msg["_id"]?.ToString(), isPrivate);
            }
            if (userId == 0 || userId == selfId) return null;   // bot 自身不发/不入库

            var displayName = FirstString(msg, "senderCard", "sender_card", "card", "senderNickname", "nickname", "senderName");
            var nickname = FirstString(msg, "senderNickname", "nickname", "senderName");

            // @bot 名字匹配（无 @ 时按 BotNames 匹配文本）
            if (!isMentioned && adapter.Config.BotNames.Count > 0)
            {
                foreach (var n in adapter.Config.BotNames)
                {
                    if (content.Contains(n, StringComparison.OrdinalIgnoreCase)) { isMentioned = true; break; }
                }
            }

            var finalContent = textBuilder.ToString().Trim();
            if (string.IsNullOrEmpty(finalContent) && attachments.Count == 0)
                return null;

            var timeMs = msg["time"]?.Value<long?>() ?? msg["timestamp"]?.Value<long?>();
            var time = timeMs.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(timeMs.Value).LocalDateTime
                : DateTime.Now;

            return new IncomingMessage
            {
                Platform = adapter.Platform,
                PlatformUserId = userId.ToString(),
                ChannelId = channelId,
                Content = finalContent,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? userId.ToString() : displayName,
                Nickname = nickname,
                IsPrivate = isPrivate,
                IsMentioned = isMentioned,
                ReplyTo = replyTo,
                QuotedContent = quotedContent,
                QuotedSenderPlatformId = quotedSenderId,
                QuotedSenderName = quotedSenderName,
                PlatformMessageId = msg["_id"]?.ToString(),
                Time = time,
                Attachments = attachments.Count > 0 ? attachments : null,
                MentionedPlatformIds = mentionedIds.Count > 0 ? mentionedIds : null
            };
        }

        public bool PassesFilter(string channelId)
        {
            var mode = adapter.Config.FilterMode.ToLowerInvariant();
            return mode switch
            {
                "whitelist" => adapter.Config.Whitelist.Contains(channelId, StringComparer.OrdinalIgnoreCase),
                "blacklist" => !adapter.Config.Blacklist.Contains(channelId, StringComparer.OrdinalIgnoreCase),
                _ => true
            };
        }

        // ── 辅助 ──

        private async Task<(MessageAttachment? Att, bool Ok)> DownloadImageAsync(string? url, bool isFace)
        {
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return (null, false);

            try
            {
                var (localPath, hash) = await ImageStorage.DownloadAndSaveAsync(url, adapter.HttpClient);
                var category = isFace ? "sticker" : "image";
                await ImageStorage.SetCategoryAsync(hash, category);
                return (new MessageAttachment
                {
                    Type = AttachmentType.Image,
                    SourceUrl = url,
                    LocalPath = localPath,
                    FileName = Path.GetFileName(localPath),
                    Hash = hash,
                    Category = category
                }, true);
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Adapter, "Icalingua图片下载失败", new { url, error = ex.Message });
                return (null, false);
            }
        }

        private static AttachmentType MediaType(string type)
        {
            if (type.StartsWith("audio", StringComparison.OrdinalIgnoreCase)) return AttachmentType.Audio;
            if (type.StartsWith("video", StringComparison.OrdinalIgnoreCase)) return AttachmentType.Video;
            return AttachmentType.File;
        }

        private static string DescribeMedia(string type, string? name)
        {
            if (type.StartsWith("audio", StringComparison.OrdinalIgnoreCase)) return "语音";
            if (type.StartsWith("video", StringComparison.OrdinalIgnoreCase)) return "视频";
            return "文件";
        }

        private static string? FirstString(JObject obj, params string[] keys)
        {
            foreach (var k in keys)
            {
                var v = obj[k]?.ToString();
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        /// <summary>URL 解码 @ 名字（encodeURIComponent 编码，且 . 替换为 %2E）。</summary>
        private static string SafeDecode(string encoded)
        {
            try { return Uri.UnescapeDataString(encoded.Replace("%2E", ".")); }
            catch { return encoded; }
        }

        /// <summary>从 _id（base64 二进制）解析发送者 QQ（§9.3）。解析失败返回 0。</summary>
        private static long ParseSenderFromId(string? id, bool isPrivate)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            try
            {
                var bytes = Convert.FromBase64String(id);
                if (bytes.Length == 21)        // 群聊：readUInt32BE(4)
                    return ReadUInt32BE(bytes, 4);
                if (bytes.Length == 17)        // 私聊 C2C：readUInt32BE(0)
                    return ReadUInt32BE(bytes, 0);
            }
            catch { }
            return 0;
        }

        private static long ReadUInt32BE(byte[] b, int offset)
        {
            return ((long)b[offset] << 24) | ((long)b[offset + 1] << 16) | ((long)b[offset + 2] << 8) | b[offset + 3];
        }
    }
}
