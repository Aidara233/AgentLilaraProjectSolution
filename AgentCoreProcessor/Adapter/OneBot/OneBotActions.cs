using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Adapter
{
    internal class OneBotActions
    {
        private readonly OneBotAdapter adapter;

        public OneBotActions(OneBotAdapter adapter)
        {
            this.adapter = adapter;
        }

        public async Task<string?> SendMessageAsync(OutgoingMessage message)
        {
            string action;
            var p = new JObject();

            if (message.ChannelId.StartsWith("group_"))
            {
                action = "send_group_msg";
                if (!long.TryParse(message.ChannelId[6..], out var groupId)) return null;
                p["group_id"] = groupId;
            }
            else if (message.ChannelId.StartsWith("private_"))
            {
                action = "send_private_msg";
                if (!long.TryParse(message.ChannelId[8..], out var userId)) return null;
                p["user_id"] = userId;
            }
            else
            {
                return null;
            }

            var segments = new JArray();

            if (!string.IsNullOrEmpty(message.ReplyTo))
            {
                segments.Add(new JObject
                {
                    ["type"] = "reply",
                    ["data"] = new JObject { ["id"] = message.ReplyTo }
                });
            }

            if (message.Segments is { Count: > 0 })
            {
                // 新路径：有序 segments（文本/图片/@ 交错）
                foreach (var seg in message.Segments)
                {
                    switch (seg.Type)
                    {
                        case SegmentType.Text:
                            if (!string.IsNullOrEmpty(seg.Text))
                                segments.Add(new JObject
                                {
                                    ["type"] = "text",
                                    ["data"] = new JObject { ["text"] = seg.Text }
                                });
                            break;
                        case SegmentType.At:
                            segments.Add(new JObject
                            {
                                ["type"] = "at",
                                ["data"] = new JObject { ["qq"] = seg.AtPlatformId }
                            });
                            break;
                        case SegmentType.Image:
                            var imgFile = EncodeFileForOneBot(seg.ImagePath, null);
                            segments.Add(new JObject
                            {
                                ["type"] = "image",
                                ["data"] = new JObject { ["file"] = imgFile }
                            });
                            break;
                        case SegmentType.Reply:
                            // reply 已在消息级处理，segments 里不应出现
                            break;
                    }
                }
            }
            else
            {
                // 旧路径：Content + Mentions + Attachments（兼容非 ChannelAccessImpl 调用方）
                var content = message.Content ?? "";
                var atDelim = BotOutputParser.AtDelimiter;
                var atPrefix = BotOutputParser.AtPrefix;

                if (content.Contains(atDelim))
                {
                    var parts = content.Split(atDelim);
                    foreach (var part in parts)
                    {
                        if (part.StartsWith(atPrefix))
                        {
                            var qq = part[atPrefix.Length..];
                            segments.Add(new JObject
                            {
                                ["type"] = "at",
                                ["data"] = new JObject { ["qq"] = qq }
                            });
                        }
                        else if (part.Length > 0)
                        {
                            segments.Add(new JObject
                            {
                                ["type"] = "text",
                                ["data"] = new JObject { ["text"] = part }
                            });
                        }
                    }
                }
                else
                {
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

                    var textContent = message.Mentions is { Count: > 0 } && !content.StartsWith(" ")
                        ? " " + content
                        : content;
                    segments.Add(new JObject
                    {
                        ["type"] = "text",
                        ["data"] = new JObject { ["text"] = textContent }
                    });
                }

                // 处理附件（追加到末尾）
                if (message.Attachments is { Count: > 0 })
                {
                    string? fileResult = null;
                    foreach (var att in message.Attachments)
                    {
                        switch (att.Type)
                        {
                            case AttachmentType.Image:
                                var imgFile = EncodeFileForOneBot(att.LocalPath, att.SourceUrl);
                                segments.Add(new JObject
                                {
                                    ["type"] = "image",
                                    ["data"] = new JObject { ["file"] = imgFile }
                                });
                                break;
                            case AttachmentType.Audio:
                                var audioFile = EncodeFileForOneBot(att.LocalPath, att.SourceUrl);
                                segments.Add(new JObject
                                {
                                    ["type"] = "record",
                                    ["data"] = new JObject { ["file"] = audioFile }
                                });
                                break;
                            case AttachmentType.File:
                                try { fileResult = await SendFileAsync(message.ChannelId, att); }
                                catch (Exception ex) { Signal.Warn(LogGroup.Adapter, "文件上传失败", new { error = ex.Message }); }
                                break;
                        }
                    }
                    // 纯文件消息（无文本内容）：跳过空 send_msg，直接返回文件上传结果
                    if (fileResult != null && segments.Count == 0 && string.IsNullOrEmpty(message.Content))
                        return fileResult;
                }
            }
            p["message"] = segments;

            var resp = await adapter.CallApiAsync(action, p);
            if (resp != null)
            {
                if (resp["retcode"]?.Value<int>() != 0)
                {
                    Signal.Warn(LogGroup.Adapter, "OneBot API返回错误", new { retcode = resp["retcode"]?.Value<int>(), action, message = resp["message"]?.ToString(), wording = resp["wording"]?.ToString() });
                }
                else
                {
                    var sentId = resp["data"]?["message_id"]?.Value<long>() ?? 0;
                    if (sentId != 0)
                    {
                        adapter.TrackSentMessage(sentId);
                        return sentId.ToString();
                    }
                    else
                    {
                        Signal.Warn(LogGroup.Adapter, "OneBot API成功但无message_id", new { action, data = resp["data"]?.ToString() });
                    }
                }
            }
            else
            {
                Signal.Warn(LogGroup.Adapter, "OneBot API无响应（超时或断连）", new { action });
            }
            return null;
        }

        // ── 第一批 API：信息查询 ──

        public async Task<JArray?> GetGroupListAsync()
        {
            var resp = await adapter.CallApiAsync("get_group_list");
            if (resp?["retcode"]?.Value<int>() == 0)
                return resp["data"] as JArray;
            return null;
        }

        public async Task<JArray?> GetGroupMemberListAsync(long groupId)
        {
            var resp = await adapter.CallApiAsync("get_group_member_list",
                new JObject { ["group_id"] = groupId });
            if (resp?["retcode"]?.Value<int>() == 0)
                return resp["data"] as JArray;
            return null;
        }

        public async Task<JArray?> GetFriendListAsync()
        {
            var resp = await adapter.CallApiAsync("get_friend_list");
            if (resp?["retcode"]?.Value<int>() == 0)
                return resp["data"] as JArray;
            return null;
        }

        // ── 第二批 API：互动操作 ──

        public async Task<bool> RecallMessageAsync(long messageId)
        {
            var resp = await adapter.CallApiAsync("delete_msg",
                new JObject { ["message_id"] = messageId });
            return resp?["retcode"]?.Value<int>() == 0;
        }

        public async Task<bool> SendPokeAsync(long userId, long? groupId = null)
        {
            var p = new JObject { ["user_id"] = userId };
            if (groupId.HasValue)
                p["group_id"] = groupId.Value;
            var resp = await adapter.CallApiAsync("send_poke", p);
            return resp?["retcode"]?.Value<int>() == 0;
        }

        // ── 第三批 API：状态设置 ──

        public async Task<bool> SetGroupCardAsync(long groupId, long userId, string card)
        {
            var resp = await adapter.CallApiAsync("set_group_card",
                new JObject { ["group_id"] = groupId, ["user_id"] = userId, ["card"] = card });
            return resp?["retcode"]?.Value<int>() == 0;
        }

        private async Task<string?> SendFileAsync(string channelId, MessageAttachment att)
        {
            var filePath = att.LocalPath ?? att.SourceUrl;
            if (string.IsNullOrEmpty(filePath)) return null;

            var fileName = att.FileName ?? Path.GetFileName(filePath);

            string action;
            var p = new JObject();

            if (channelId.StartsWith("group_"))
            {
                action = "upload_group_file";
                if (!long.TryParse(channelId[6..], out var groupId)) return null;
                p["group_id"] = groupId;
            }
            else if (channelId.StartsWith("private_"))
            {
                action = "upload_private_file";
                if (!long.TryParse(channelId[8..], out var userId)) return null;
                p["user_id"] = userId;
            }
            else
            {
                return null;
            }

            // NapCat 在虚拟机中运行，无法访问主机文件系统，需要 base64 编码
            p["file"] = EncodeFileForOneBot(filePath, null);
            p["name"] = fileName;

            var resp = await adapter.CallApiAsync(action, p);
            if (resp != null)
            {
                if (resp["retcode"]?.Value<int>() != 0)
                {
                    Signal.Warn(LogGroup.Adapter, "文件上传API返回错误", new
                    {
                        action,
                        retcode = resp["retcode"]?.Value<int>(),
                        message = resp["message"]?.ToString(),
                        wording = resp["wording"]?.ToString(),
                        filePath,
                        fileName
                    });
                    return null;
                }
                return "0"; // upload_group_file 不返回 message_id，用 "0" 表示成功
            }

            Signal.Warn(LogGroup.Adapter, "文件上传API无响应", new { action, filePath });
            return null;
        }

        private static string EncodeFileForOneBot(string? localPath, string? sourceUrl)
        {
            if (localPath != null && File.Exists(localPath))
            {
                var bytes = File.ReadAllBytes(localPath);
                return $"base64://{Convert.ToBase64String(bytes)}";
            }
            return sourceUrl ?? "";
        }
    }
}
