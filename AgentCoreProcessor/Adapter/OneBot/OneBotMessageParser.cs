using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Adapter
{
    internal class OneBotMessageParser
    {
        private readonly OneBotAdapter adapter;

        private readonly HashSet<long> recentMessageIds = new();
        private DateTime lastMessageIdCleanup = DateTime.Now;

        public OneBotMessageParser(OneBotAdapter adapter)
        {
            this.adapter = adapter;
        }

        public IncomingMessage? HandleEvent(JObject data)
        {
            var postType = data["post_type"]?.ToString();
            if (postType == "message")
            {
                var messageId = data["message_id"]?.Value<long>() ?? 0;
                if (messageId != 0)
                {
                    lock (recentMessageIds)
                    {
                        if ((DateTime.Now - lastMessageIdCleanup).TotalSeconds > 60)
                        {
                            recentMessageIds.Clear();
                            lastMessageIdCleanup = DateTime.Now;
                        }
                        if (!recentMessageIds.Add(messageId))
                            return null;
                    }
                }
                return ParseMessageEventAsync(data).GetAwaiter().GetResult();
            }
            if (postType == "notice")
            {
                return ParseNoticeEvent(data);
            }
            return null;
        }

        public async Task<IncomingMessage?> HandleEventAsync(JObject data)
        {
            var postType = data["post_type"]?.ToString();
            if (postType == "message")
            {
                var messageId = data["message_id"]?.Value<long>() ?? 0;
                if (messageId != 0)
                {
                    lock (recentMessageIds)
                    {
                        if ((DateTime.Now - lastMessageIdCleanup).TotalSeconds > 60)
                        {
                            recentMessageIds.Clear();
                            lastMessageIdCleanup = DateTime.Now;
                        }
                        if (!recentMessageIds.Add(messageId))
                            return null;
                    }
                }
                return await ParseMessageEventAsync(data);
            }
            if (postType == "notice")
            {
                return ParseNoticeEvent(data);
            }
            return null;
        }

        private async Task<IncomingMessage?> ParseMessageEventAsync(JObject data)
        {
            var config = adapter.Config;
            var selfId = adapter.SelfId;
            var userId = data["user_id"]?.Value<long>() ?? 0;

            if (userId == selfId) return null;

            var messageType = data["message_type"]?.ToString();
            bool isPrivate = messageType == "private";

            string channelId;
            if (isPrivate)
                channelId = $"private_{userId}";
            else
                channelId = $"group_{data["group_id"]?.Value<long>() ?? 0}";

            if (!PassesFilter(channelId)) return null;

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
                                var (localPath, imgHash) = await ImageStorage.DownloadAndSaveAsync(imageUrl, adapter.HttpClient);

                                // 表情包识别：sub_type=1 或有 emoji_id → sticker
                                var subType = segData["sub_type"]?.Value<int>() ?? 0;
                                var emojiId = segData["emoji_id"]?.ToString();
                                var imgCategory = (subType == 1 || !string.IsNullOrEmpty(emojiId))
                                    ? "sticker" : "image";

                                // 更新 ImageRecord 的 category
                                await ImageStorage.SetCategoryAsync(imgHash, imgCategory);

                                attachments ??= new List<MessageAttachment>();
                                attachments.Add(new MessageAttachment
                                {
                                    Type = AttachmentType.Image,
                                    SourceUrl = imageUrl,
                                    LocalPath = localPath,
                                    FileName = Path.GetFileName(localPath),
                                    Hash = imgHash,
                                    Category = imgCategory
                                });
                            }
                            catch (Exception ex)
                            {
                                FrameworkLogger.Log("OneBotParser", $"图片下载失败: {ex.Message}");
                            }
                        }
                        break;
                    case "record":
                        textBuilder.Append("[语音消息]");
                        attachments ??= new List<MessageAttachment>();
                        attachments.Add(new MessageAttachment
                        {
                            Type = AttachmentType.Audio,
                            SourceUrl = segData["url"]?.ToString(),
                            FileName = segData["file"]?.ToString()
                        });
                        break;
                    case "video":
                        textBuilder.Append("[视频消息]");
                        attachments ??= new List<MessageAttachment>();
                        attachments.Add(new MessageAttachment
                        {
                            Type = AttachmentType.Video,
                            SourceUrl = segData["url"]?.ToString(),
                            FileName = segData["file"]?.ToString()
                        });
                        break;
                    case "file":
                        var fName = segData["name"]?.ToString() ?? segData["file"]?.ToString() ?? "未知文件";
                        textBuilder.Append($"[文件: {fName}]");
                        attachments ??= new List<MessageAttachment>();
                        attachments.Add(new MessageAttachment
                        {
                            Type = AttachmentType.File,
                            SourceUrl = segData["url"]?.ToString(),
                            FileName = fName,
                            FileSize = segData["size"]?.Value<long>()
                        });
                        break;
                }
            }

            var content = textBuilder.ToString().Trim();
            if (string.IsNullOrEmpty(content) && (attachments == null || attachments.Count == 0))
                return null;

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

            if (!isMentioned && replyTo != null && long.TryParse(replyTo, out var replyMsgId))
            {
                if (adapter.IsSentMessage(replyMsgId))
                    isMentioned = true;
            }

            var sender = data["sender"] as JObject;
            var nickname = sender?["nickname"]?.ToString();
            var card = sender?["card"]?.ToString();
            var displayName = !string.IsNullOrWhiteSpace(card) ? card : nickname;

            string? quotedContent = null;
            if (replyTo != null)
            {
                try
                {
                    var resp = await adapter.CallApiAsync("get_msg", new JObject { ["message_id"] = long.Parse(replyTo) });
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
                Platform = adapter.Platform,
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

        public bool PassesFilter(string channelId)
        {
            var config = adapter.Config;
            return config.FilterMode.ToLower() switch
            {
                "whitelist" => config.Whitelist.Contains(channelId, StringComparer.OrdinalIgnoreCase),
                "blacklist" => !config.Blacklist.Contains(channelId, StringComparer.OrdinalIgnoreCase),
                _ => true
            };
        }

        private IncomingMessage? ParseNoticeEvent(JObject data)
        {
            var noticeType = data["notice_type"]?.ToString();
            var subType = data["sub_type"]?.ToString();
            var selfId = adapter.SelfId;
            var groupId = data["group_id"]?.Value<long>() ?? 0;
            var userId = data["user_id"]?.Value<long>() ?? 0;

            string? channelId = groupId > 0 ? $"group_{groupId}" : null;
            if (channelId == null) return null;
            if (!PassesFilter(channelId)) return null;

            string content;
            bool isMentioned = false;

            switch (noticeType)
            {
                case "notify" when subType == "poke":
                    var targetId = data["target_id"]?.Value<long>() ?? 0;
                    if (targetId == selfId)
                    {
                        content = $"【系统】{GetUserLabel(data, userId)} 戳了你";
                        isMentioned = true;
                    }
                    else
                    {
                        content = $"【系统】{GetUserLabel(data, userId)} 戳了 {targetId}";
                    }
                    break;

                case "group_ban":
                    var banUserId = data["user_id"]?.Value<long>() ?? 0;
                    var duration = data["duration"]?.Value<long>() ?? 0;
                    var operatorId = data["operator_id"]?.Value<long>() ?? 0;
                    if (banUserId == selfId)
                    {
                        content = duration > 0
                            ? $"【系统】你被 {operatorId} 禁言了 {duration} 秒"
                            : $"【系统】你被 {operatorId} 解除了禁言";
                        isMentioned = true;
                    }
                    else
                    {
                        content = duration > 0
                            ? $"【系统】{banUserId} 被 {operatorId} 禁言了 {duration} 秒"
                            : $"【系统】{banUserId} 被 {operatorId} 解除了禁言";
                    }
                    break;

                case "group_recall":
                    var recallOperator = data["operator_id"]?.Value<long>() ?? 0;
                    content = $"【系统】{recallOperator} 撤回了一条消息";
                    break;

                case "group_upload":
                    var file = data["file"] as JObject;
                    var fileName = file?["name"]?.ToString() ?? "未知文件";
                    content = $"【系统】{GetUserLabel(data, userId)} 上传了文件: {fileName}";
                    break;

                default:
                    return null;
            }

            return new IncomingMessage
            {
                Platform = adapter.Platform,
                PlatformUserId = userId.ToString(),
                ChannelId = channelId,
                Content = content,
                IsPrivate = false,
                IsMentioned = isMentioned,
                IsSystemEvent = true,
                Time = DateTime.Now
            };
        }

        private static string GetUserLabel(JObject data, long userId)
        {
            var sender = data["sender"] as JObject;
            var card = sender?["card"]?.ToString();
            var nickname = sender?["nickname"]?.ToString();
            if (!string.IsNullOrWhiteSpace(card)) return card;
            if (!string.IsNullOrWhiteSpace(nickname)) return nickname;
            return userId.ToString();
        }
    }
}
