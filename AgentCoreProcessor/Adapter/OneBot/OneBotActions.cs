using System;
using System.Collections.Generic;
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
            p["message"] = segments;

            // 处理附件
            if (message.Attachments is { Count: > 0 })
            {
                foreach (var att in message.Attachments)
                {
                    switch (att.Type)
                    {
                        case AttachmentType.Image:
                            var imgFile = att.LocalPath ?? att.SourceUrl ?? "";
                            segments.Add(new JObject
                            {
                                ["type"] = "image",
                                ["data"] = new JObject { ["file"] = imgFile }
                            });
                            break;
                        case AttachmentType.Audio:
                            var audioFile = att.LocalPath ?? att.SourceUrl ?? "";
                            segments.Add(new JObject
                            {
                                ["type"] = "record",
                                ["data"] = new JObject { ["file"] = audioFile }
                            });
                            break;
                        case AttachmentType.File:
                            // 文件上传走单独 API，不走 message segment
                            try { await SendFileAsync(message.ChannelId, att); }
                            catch (Exception ex) { Signal.Warn(LogGroup.Adapter, "文件上传失败", new { error = ex.Message }); }
                            break;
                    }
                }
            }

            var resp = await adapter.CallApiAsync(action, p);
            if (resp != null)
            {
                if (resp["retcode"]?.Value<int>() != 0)
                {
                    Signal.Warn(LogGroup.Adapter, "OneBot API返回错误", new { retcode = resp["retcode"]?.Value<int>(), action });
                }
                else
                {
                    var sentId = resp["data"]?["message_id"]?.Value<long>() ?? 0;
                    if (sentId != 0)
                    {
                        adapter.TrackSentMessage(sentId);
                        return sentId.ToString();
                    }
                }
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

        private async Task SendFileAsync(string channelId, MessageAttachment att)
        {
            var filePath = att.LocalPath ?? att.SourceUrl;
            if (string.IsNullOrEmpty(filePath)) return;

            var fileName = att.FileName ?? System.IO.Path.GetFileName(filePath);

            if (channelId.StartsWith("group_"))
            {
                var groupId = long.Parse(channelId[6..]);
                await adapter.CallApiAsync("upload_group_file", new JObject
                {
                    ["group_id"] = groupId,
                    ["file"] = filePath,
                    ["name"] = fileName
                });
            }
            else if (channelId.StartsWith("private_"))
            {
                var userId = long.Parse(channelId[8..]);
                await adapter.CallApiAsync("upload_private_file", new JObject
                {
                    ["user_id"] = userId,
                    ["file"] = filePath,
                    ["name"] = fileName
                });
            }
        }
    }
}
