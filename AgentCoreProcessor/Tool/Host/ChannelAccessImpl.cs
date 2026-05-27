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
                PlatformChannelId = ch.Name
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

            var (segments, replyTo) = await ParseOutputTags(channelId, content);

            // 兼容旧路径：提取纯文本和 mention 列表
            var textContent = "";
            var mentions = new List<string>();
            if (segments.Count > 0)
            {
                foreach (var seg in segments)
                {
                    if (seg.Type == SegmentType.Text)
                        textContent += seg.Text;
                    else if (seg.Type == SegmentType.At)
                        mentions.Add(seg.AtPlatformId!);
                }
            }

            var sentId = await adapter.SendMessageAsync(new OutgoingMessage
            {
                ChannelId = channel.Name,
                Content = textContent,
                ReplyTo = replyTo,
                Mentions = mentions.Count > 0 ? mentions : null,
                Segments = segments.Count > 0 ? segments : null
            });

            await _ctx.Session.SaveBotMessageAsync(channelId, textContent, sentId);
            return sentId;
        }

        public async Task<string?> SendMediaAsync(int channelId, string mediaType, string identifier)
        {
            var channel = await _ctx.Session.GetChannelByIdAsync(channelId);
            if (channel == null)
            {
                Logging.Signal.Warn(Logging.LogGroup.Adapter, "SendMedia: channel not found", new { channelId });
                return null;
            }

            var resolvedPath = ResolveSafePath(identifier);
            if (resolvedPath == null)
            {
                Logging.Signal.Warn(Logging.LogGroup.Adapter, "SendMedia: path resolve failed", new { identifier, workspace = Config.PathConfig.WorkspacePath });
                return null;
            }

            var adapter = _ctx.Adapters.ResolveByChannelId(channel.Name);
            if (adapter == null)
            {
                Logging.Signal.Warn(Logging.LogGroup.Adapter, "SendMedia: adapter not found", new { channelName = channel.Name });
                return null;
            }

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
                    LocalPath = resolvedPath,
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
            var resolvedPath = ResolveSafePath(filePath);
            if (resolvedPath == null) return null;

            var channel = await _ctx.Session.GetChannelByIdAsync(channelId);
            if (channel == null) return null;

            var adapter = _ctx.Adapters.ResolveByChannelId(channel.Name);
            if (adapter == null) return null;

            var name = fileName ?? System.IO.Path.GetFileName(resolvedPath);
            var sentId = await adapter.SendMessageAsync(new OutgoingMessage
            {
                ChannelId = channel.Name,
                Content = "",
                Attachments = new List<MessageAttachment>
                {
                    new()
                    {
                        Type = AttachmentType.File,
                        LocalPath = resolvedPath,
                        FileName = name
                    }
                }
            });

            await _ctx.Session.SaveBotMessageAsync(channelId, $"[发送文件] {name}", sentId);
            return sentId;
        }

        // ── 辅助：解析输出标签 ──

        private const char AtDelimiter = '\x01';
        private const string AtPrefix = "AT:";
        private static readonly Regex OutputTagRegex =
            new(@"(?:<reply\s+id=""([^""]+)""\s*/>)|(?:<img\s+hash=""([^""]+)""\s*/>)|(?:<img\s+work=""([^""]+)""\s*/>)|(?:<at\s+user=""([^""]+)""\s*/>)",
                RegexOptions.Compiled);

        /// <summary>
        /// 解析 content 中的标签，返回有序 segmens（文本/图片/@/回复 交错排列）。
        /// </summary>
        private async Task<(List<MessageSegment> Segments, string? ReplyTo)>
            ParseOutputTags(int channelId, string raw)
        {
            string? replyTo = null;

            // 先统一找所有标签位置
            var tagMatches = OutputTagRegex.Matches(raw);
            if (tagMatches.Count == 0 && string.IsNullOrWhiteSpace(raw))
                return (new List<MessageSegment>(), replyTo);

            // 构建参与者映射（仅当有 <at/> 标签时）
            Dictionary<string, string>? nameMap = null;
            var hasAt = false;
            foreach (Match m in tagMatches)
            {
                if (m.Groups[4].Success) { hasAt = true; break; }
            }
            if (hasAt)
                nameMap = await BuildParticipantMapAsync(channelId);

            var segments = new List<MessageSegment>();
            var pos = 0;

            foreach (Match m in tagMatches.OrderBy(m => m.Index))
            {
                // 标签前的文本
                if (m.Index > pos)
                {
                    var text = raw[pos..m.Index].Trim();
                    if (text.Length > 0)
                    {
                        // 合并连着的纯文本（<at/> 转换后的形式）
                        segments.Add(new MessageSegment { Type = SegmentType.Text, Text = text });
                    }
                }

                if (m.Groups[1].Success) // <reply id="..."/>
                {
                    replyTo = m.Groups[1].Value;
                    // reply 不作为 segment 内容，仅作为消息级元数据
                }
                else if (m.Groups[2].Success || m.Groups[3].Success) // <img hash="..."/> or <img work="..."/>
                {
                    string? imagePath = null;
                    if (m.Groups[2].Success)
                    {
                        var record = await ImageStorage.GetByHashAsync(m.Groups[2].Value);
                        imagePath = record?.LocalPath;
                    }
                    else
                    {
                        imagePath = ResolveWorkspacePath(m.Groups[3].Value);
                    }
                    if (imagePath != null)
                        segments.Add(new MessageSegment { Type = SegmentType.Image, ImagePath = imagePath });
                }
                else if (m.Groups[4].Success) // <at user="..."/>
                {
                    var userName = m.Groups[4].Value;
                    var platformId = nameMap?.GetValueOrDefault(userName);
                    if (platformId != null)
                        segments.Add(new MessageSegment { Type = SegmentType.At, AtPlatformId = platformId });
                    else
                        segments.Add(new MessageSegment { Type = SegmentType.Text, Text = $"@{userName} " });
                }

                pos = m.Index + m.Length;
            }

            // 结尾剩余文本
            if (pos < raw.Length)
            {
                var tail = raw[pos..].Trim();
                if (tail.Length > 0)
                    segments.Add(new MessageSegment { Type = SegmentType.Text, Text = tail });
            }

            return (segments, replyTo);
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

        /// <summary>解析安全路径：hash:xxx → ImageStorage, 其他 → Workspace 相对路径。</summary>
        private static string? ResolveSafePath(string identifier)
        {
            if (identifier.StartsWith("hash:"))
            {
                var hash = identifier[5..];
                var record = ImageStorage.GetByHashAsync(hash).GetAwaiter().GetResult();
                return record?.LocalPath;
            }
            return ResolveWorkspacePath(identifier);
        }

        /// <summary>将相对路径解析到 Workspace，校验不越界。</summary>
        private static string? ResolveWorkspacePath(string relativePath)
        {
            var workspace = Path.GetFullPath(Config.PathConfig.WorkspacePath);
            Directory.CreateDirectory(workspace);
            var full = Path.GetFullPath(Path.Combine(workspace, relativePath));
            if (!full.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
                return null;
            return full;
        }
    }
}
