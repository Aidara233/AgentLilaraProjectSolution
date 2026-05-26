using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    internal class ChannelAccessImpl : IChannelAccess
    {
        private readonly ISystemContext _ctx;

        public ChannelAccessImpl(ISystemContext ctx)
        {
            _ctx = ctx;
        }

        public async Task<List<ChannelSummary>> GetAllChannelsAsync()
        {
            var channels = await _ctx.Session.GetAllChannelsAsync();
            return channels.Select(c => new ChannelSummary
            {
                Id = c.Id,
                Name = c.Name,
                Platform = "",
                MessageCount = 0,
                HasActiveEngine = _ctx.HasActiveEngine($"Channel:{c.Id}")
            }).ToList();
        }

        public async Task<ChannelDetail?> GetChannelDetailAsync(int channelId)
        {
            var channels = await _ctx.Session.GetAllChannelsAsync();
            var ch = channels.FirstOrDefault(c => c.Id == channelId);
            if (ch == null) return null;
            return new ChannelDetail
            {
                Id = ch.Id,
                Name = ch.Name,
                Platform = "",
                PlatformChannelId = ""
            };
        }

        public async Task<List<MessageSummary>> GetMessagesAsync(int channelId, int count = 20)
        {
            var messages = await _ctx.Session.GetContextByChannelAsync(channelId, limit: count);
            return messages.Select(m => new MessageSummary
            {
                Id = m.Id,
                UserName = m.IsFromBot ? "Lilara"
                    : !string.IsNullOrEmpty(m.SenderName) ? m.SenderName
                    : $"User:{m.UserId}",
                Content = m.Content,
                Timestamp = m.Time
            }).ToList();
        }

        public async Task UpdateAffinityAsync(int channelId, float delta)
        {
            var channels = await _ctx.Session.GetAllChannelsAsync();
            var ch = channels.FirstOrDefault(c => c.Id == channelId);
            if (ch == null) return;
            ch.Affinity = System.Math.Clamp(ch.Affinity + delta, 0.1f, 3.0f);
            await _ctx.Session.UpdateChannelAsync(ch);
        }

        // ── 消息输出 ──

        public async Task<string?> SendMessageAsync(int channelId, string content)
        {
            var channel = await _ctx.Session.GetChannelByIdAsync(channelId);
            if (channel == null) return null;

            var adapter = _ctx.Adapters.ResolveByChannelId(channel.Name);
            if (adapter == null) return null;

            var (parsedContent, replyTo, mentions, imagePaths) = await ParseOutputTags(channelId, content);

            var attachments = new List<MessageAttachment>();
            if (imagePaths != null)
            {
                foreach (var path in imagePaths)
                {
                    attachments.Add(new MessageAttachment
                    {
                        Type = AttachmentType.Image,
                        LocalPath = IsLocalPath(path) ? path : null,
                        SourceUrl = IsLocalPath(path) ? null : path
                    });
                }
            }

            var sentId = await adapter.SendMessageAsync(new OutgoingMessage
            {
                ChannelId = channel.Name,
                Content = parsedContent,
                ReplyTo = replyTo,
                Mentions = mentions,
                Attachments = attachments.Count > 0 ? attachments : null
            });

            await _ctx.Session.SaveBotMessageAsync(channelId, parsedContent, sentId);
            return sentId;
        }

        public async Task<string?> SendMediaAsync(int channelId, string mediaType, string pathOrUrl)
        {
            var channel = await _ctx.Session.GetChannelByIdAsync(channelId);
            if (channel == null) return null;

            var adapter = _ctx.Adapters.ResolveByChannelId(channel.Name);
            if (adapter == null) return null;

            var attachmentType = mediaType switch
            {
                "voice" => AttachmentType.Audio,
                "video" => AttachmentType.Video,
                "sticker" => AttachmentType.Image,
                _ => AttachmentType.Image
            };

            var attachments = new List<MessageAttachment>
            {
                new()
                {
                    Type = attachmentType,
                    LocalPath = IsLocalPath(pathOrUrl) ? pathOrUrl : null,
                    SourceUrl = IsLocalPath(pathOrUrl) ? null : pathOrUrl,
                    Category = mediaType == "sticker" ? "sticker" : null
                }
            };

            var sentId = await adapter.SendMessageAsync(new OutgoingMessage
            {
                ChannelId = channel.Name,
                Content = "",
                Attachments = attachments
            });

            var desc = $"[发送{mediaType}]";
            await _ctx.Session.SaveBotMessageAsync(channelId, desc, sentId);
            return sentId;
        }

        public async Task<string?> SendFileAsync(int channelId, string filePath, string? fileName = null)
        {
            var channel = await _ctx.Session.GetChannelByIdAsync(channelId);
            if (channel == null) return null;

            var adapter = _ctx.Adapters.ResolveByChannelId(channel.Name);
            if (adapter == null) return null;

            var sentId = await adapter.SendMessageAsync(new OutgoingMessage
            {
                ChannelId = channel.Name,
                Content = "",
                Attachments = new List<MessageAttachment>
                {
                    new()
                    {
                        Type = AttachmentType.File,
                        LocalPath = filePath,
                        FileName = fileName ?? System.IO.Path.GetFileName(filePath)
                    }
                }
            });

            await _ctx.Session.SaveBotMessageAsync(channelId, $"[发送文件] {fileName ?? filePath}", sentId);
            return sentId;
        }

        // ── 辅助：解析输出标签 ──

        private const char AtDelimiter = '\x01';
        private const string AtPrefix = "AT:";
        private static readonly Regex AtTagRegex =
            new(@"<at\s+user=""([^""]+)""\s*/>", RegexOptions.Compiled);
        private static readonly Regex ReplyTagRegex =
            new(@"<reply\s+id=""([^""]+)""\s*/>", RegexOptions.Compiled);
        private static readonly Regex ImgTagRegex =
            new(@"<img\s+path=""([^""]+)""\s*/>", RegexOptions.Compiled);

        /// <summary>
        /// 解析 content 中的 <at/> <reply/> <img/> 标签。
        /// </summary>
        private async Task<(string Content, string? ReplyTo, List<string>? Mentions, List<string>? ImagePaths)>
            ParseOutputTags(int channelId, string raw)
        {
            string? replyTo = null;
            List<string>? mentions = null;
            List<string>? imagePaths = null;

            // <reply/>
            var replyMatch = ReplyTagRegex.Match(raw);
            if (replyMatch.Success)
            {
                replyTo = replyMatch.Groups[1].Value;
                raw = raw.Remove(replyMatch.Index, replyMatch.Length).TrimStart();
            }

            // <img/> — 提取路径后移除标记
            var imgMatches = ImgTagRegex.Matches(raw);
            if (imgMatches.Count > 0)
            {
                imagePaths = new List<string>();
                foreach (Match m in imgMatches)
                    imagePaths.Add(m.Groups[1].Value);
                raw = ImgTagRegex.Replace(raw, "").Trim();
            }

            // 构建参与者名→platformId 映射
            var nameToPlatformId = await BuildParticipantMapAsync(channelId);

            // <at user="name"/>
            raw = AtTagRegex.Replace(raw, match =>
            {
                var userName = match.Groups[1].Value;
                if (nameToPlatformId.TryGetValue(userName, out var platformId))
                {
                    mentions ??= new List<string>();
                    if (!mentions.Contains(platformId)) mentions.Add(platformId);
                    return $"{AtDelimiter}{AtPrefix}{platformId}{AtDelimiter}";
                }
                return $"@{userName} ";
            });

            return (raw.Trim(), replyTo, mentions, imagePaths);
        }

        /// <summary>
        /// 从频道最近消息构建参与者名→platformId 映射。
        /// </summary>
        private async Task<Dictionary<string, string>> BuildParticipantMapAsync(int channelId)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var msgs = await _ctx.Session.GetContextByChannelAsync(channelId, limit: 30);
                var userIds = msgs.Where(m => m.UserId > 0).Select(m => m.UserId).Distinct();
                foreach (var uid in userIds)
                {
                    var user = await _ctx.Session.GetUserByIdAsync(uid);
                    if (user == null) continue;
                    var person = await _ctx.Session.GetPersonByIdAsync(user.PersonId);

                    // DisplayName
                    var displayName = !string.IsNullOrEmpty(person?.Name) ? person.Name
                        : !string.IsNullOrEmpty(user.DisplayName) ? user.DisplayName
                        : user.PlatformId;
                    map.TryAdd(displayName, user.PlatformId);

                    // PlatformId itself
                    map.TryAdd(user.PlatformId, user.PlatformId);

                    // Aliases
                    if (!string.IsNullOrEmpty(person?.Aliases))
                    {
                        foreach (var alias in person.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            map.TryAdd(alias, user.PlatformId);
                    }
                }
            }
            catch { /* 参与者映射失败不阻止发送 */ }
            return map;
        }

        private static bool IsLocalPath(string path)
        {
            return path.Length > 1 && (path[1] == ':' || path.StartsWith('/') || path.StartsWith("\\\\"));
        }
    }
}
