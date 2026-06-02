using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json;
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
                            var imgFile = await EncodeFileForOneBotAsync(seg.ImagePath, null);
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
                                var imgFile = await EncodeFileForOneBotAsync(att.LocalPath, att.SourceUrl);
                                segments.Add(new JObject
                                {
                                    ["type"] = "image",
                                    ["data"] = new JObject { ["file"] = imgFile }
                                });
                                break;
                            case AttachmentType.Audio:
                                var audioFile = await EncodeFileForOneBotAsync(att.LocalPath, att.SourceUrl);
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
                    // 纯文件消息（无文本、无@、无引用）：跳过空 send_msg，直接返回文件上传结果
                    if (fileResult != null
                        && string.IsNullOrEmpty(message.Content)
                        && message.Mentions is not { Count: > 0 }
                        && string.IsNullOrEmpty(message.ReplyTo))
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

        // ── 第四批 API：群文件 ──

        public async Task<string?> GetGroupFileListSummaryAsync(long groupId, string? folderId = null)
        {
            JToken? data;
            if (string.IsNullOrEmpty(folderId) || folderId == "/")
                data = await GetGroupRootFilesAsync(groupId);
            else
                data = await GetGroupFilesByFolderAsync(groupId, folderId);

            if (data == null) return null;
            return SummarizeFileList(data);
        }

        private static string SummarizeFileList(JToken data)
        {
            var files = data["files"] as JArray ?? new JArray();
            var folders = data["folders"] as JArray ?? new JArray();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[群文件] 共 {folders.Count} 个文件夹, {files.Count} 个文件");

            // 文件夹列表
            foreach (var f in folders)
            {
                var fname = f["folder_name"]?.ToString() ?? "?";
                var fid = f["folder_id"]?.ToString() ?? "";
                sb.AppendLine($"  [D] {fname} (folder_id={fid})");
            }

            // 文件列表：按 modify_time 降序取最近 30 个
            var recent = files
                .Select(f => new
                {
                    Name = f["file_name"]?.ToString() ?? "?",
                    Fid = f["file_id"]?.ToString() ?? "",
                    Busid = f["busid"]?.Value<int>() ?? 102,
                    Size = f["file_size"]?.Value<long>() ?? f["size"]?.Value<long>() ?? 0,
                    Uploader = f["uploader_name"]?.ToString() ?? "",
                    Mtime = f["modify_time"]?.Value<long>() ?? 0
                })
                .OrderByDescending(f => f.Mtime)
                .Take(30)
                .ToList();

            foreach (var f in recent)
            {
                var sizeStr = f.Size >= 1_000_000 ? $"{f.Size / 1_000_000.0:F1}MB"
                    : f.Size >= 1_000 ? $"{f.Size / 1_000.0:F1}KB"
                    : $"{f.Size}B";
                sb.AppendLine($"  {f.Name} ({sizeStr}) file_id={f.Fid} busid={f.Busid}");
            }

            if (files.Count > 30)
                sb.AppendLine($"  ... 还有 {files.Count - 30} 个文件未显示");

            return sb.ToString().TrimEnd();
        }

        private async Task<JToken?> GetGroupRootFilesAsync(long groupId)
        {
            var resp = await adapter.CallApiAsync("get_group_root_files",
                new JObject { ["group_id"] = groupId });
            if (resp?["retcode"]?.Value<int>() == 0)
                return resp["data"];
            return null;
        }

        public async Task<JToken?> GetGroupFilesByFolderAsync(long groupId, string folderId)
        {
            var resp = await adapter.CallApiAsync("get_group_files_by_folder",
                new JObject { ["group_id"] = groupId, ["folder_id"] = folderId });
            if (resp?["retcode"]?.Value<int>() == 0)
                return resp["data"];
            return null;
        }

        public async Task<string?> GetGroupFileUrlAsync(long groupId, string fileId, int busid)
        {
            var resp = await adapter.CallApiAsync("get_group_file_url",
                new JObject { ["group_id"] = groupId, ["file_id"] = fileId, ["busid"] = busid });
            if (resp?["retcode"]?.Value<int>() == 0)
                return resp["data"]?["url"]?.ToString();
            return null;
        }

        /// <summary>
        /// 通过 NapCat HTTP /get_private_file_url 获取私聊文件下载 URL。
        /// </summary>
        public async Task<string?> GetChatFileUrlAsync(string fileId, string? privateUserId = null)
        {
            var param = new JObject { ["file_id"] = fileId };
            if (!string.IsNullOrEmpty(privateUserId))
                param["user_id"] = privateUserId;

            // 使用 HTTP API（非 WebSocket），/get_private_file_url 是 NapCat 的 HTTP 扩展端点
            var baseUrl = adapter.Config.HttpUrl.TrimEnd('/');
            var url = $"{baseUrl}/get_private_file_url";
            var token = !string.IsNullOrEmpty(adapter.Config.HttpToken) ? adapter.Config.HttpToken : adapter.Config.Token;
            if (!string.IsNullOrEmpty(token))
                url += $"?access_token={Uri.EscapeDataString(token)}";

            var content = new StringContent(param.ToString(Formatting.None),
                Encoding.UTF8, "application/json");

            using var resp = await adapter.HttpClient.PostAsync(url, content);
            var body = await resp.Content.ReadAsStringAsync();
            var dbgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_get_private.log");
            File.AppendAllText(dbgPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [POST /get_private_file_url] req={param.ToString(Formatting.None)} status={resp.StatusCode} resp={body}\n");
            var json = JObject.Parse(body);
            if (json["retcode"]?.Value<int>() == 0)
                return json["data"]?["url"]?.ToString();
            return null;
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
            p["file"] = await EncodeFileForOneBotAsync(filePath, null);
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

        private static async Task<string> EncodeFileForOneBotAsync(string? localPath, string? sourceUrl)
        {
            if (localPath != null && File.Exists(localPath))
            {
                var bytes = await File.ReadAllBytesAsync(localPath);
                var base64 = await Task.Run(() => Convert.ToBase64String(bytes));
                return $"base64://{base64}";
            }
            return sourceUrl ?? "";
        }
    }
}
