using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 要直传给模型的图片信息。
    /// </summary>
    internal sealed class ImageEmbed
    {
        public required int ImageId { get; init; }
        public required string Path { get; init; }
    }

    /// <summary>
    /// XML 格式上下文构建器。从频道消息历史构建模型输入的 XML 上下文。
    /// 图片处理规则：
    ///   new 块普通图 → 直传原图
    ///   new 块表情包有描述 → 描述标记
    ///   history 块有描述 → 描述标记
    ///   history 块无描述 → 直传原图（受大小/数量限制）
    ///   quoted-context 被引用 → 直传原图
    /// </summary>
    internal class ContextBuilder
    {
        private readonly SessionManager session;
        private readonly int channelId;

        private const int MaxQuoteDepth = 2;
        private static readonly Regex ImgPlaceholderRegex = new(@"\[IMG:(\d+)\]", RegexOptions.Compiled);

        // 每次 BuildContextXmlAsync 调用期间的临时状态
        private List<ImageEmbed> _embeds = new();
        private long _totalEmbedSize;

        public ContextBuilder(SessionManager session, int channelId)
        {
            this.session = session;
            this.channelId = channelId;
        }

        // PLACEHOLDER_CONTINUE

        public async Task<(string Xml, List<ImageEmbed> Embeds)> BuildContextXmlAsync(
            List<(IncomingMessage Message, SessionContext Context)> batch,
            List<UserMessage> recentMessages,
            Dictionary<int, ParticipantInfo> participants)
        {
            var sb = new StringBuilder();
            _embeds = new List<ImageEmbed>();
            _totalEmbedSize = 0;
            var shortNames = ResolveShortNames(participants);

            sb.AppendLine("<participants>");
            foreach (var (userId, info) in participants)
            {
                var name = SanitizeAttr(shortNames.GetValueOrDefault(userId, info.DisplayName));
                var nick = SanitizeAttr(info.Nickname);
                var memo = SanitizeAttr(string.IsNullOrEmpty(info.Memo) ? "还不太了解" : info.Memo);
                var relation = TrustLevelToRelation(info.TrustLevel);
                var roleAttr = info.PermissionLevel >= Database.PermissionLevel.Admin ? " role=\"管理员\"" : "";
                sb.AppendLine($"  <user name=\"{name}\" nickname=\"{nick}\" qq=\"{info.PlatformId}\" relation=\"{relation}\"{roleAttr} memo=\"{memo}\"/>");
            }
            sb.AppendLine("</participants>");

            var batchTicks = new HashSet<long>(batch.Select(b => b.Message.Time.Ticks));

            int lastBotIndex = -1;
            for (int i = recentMessages.Count - 1; i >= 0; i--)
            {
                if (recentMessages[i].IsFromBot) { lastBotIndex = i; break; }
            }

            var unrespondedMessages = new List<UserMessage>();
            var historyMessages = new List<UserMessage>();
            for (int i = 0; i < recentMessages.Count; i++)
            {
                if (batchTicks.Contains(recentMessages[i].Time.Ticks)) continue;
                if (i > lastBotIndex && lastBotIndex >= 0 && !recentMessages[i].IsFromBot)
                    unrespondedMessages.Add(recentMessages[i]);
                else if (lastBotIndex < 0 && !recentMessages[i].IsFromBot)
                    unrespondedMessages.Add(recentMessages[i]);
                else
                    historyMessages.Add(recentMessages[i]);
            }

            var contextIds = new HashSet<string>();
            foreach (var m in recentMessages)
                if (!string.IsNullOrEmpty(m.PlatformMessageId)) contextIds.Add(m.PlatformMessageId);
            foreach (var (msg, _) in batch)
                if (!string.IsNullOrEmpty(msg.PlatformMessageId)) contextIds.Add(msg.PlatformMessageId);

            var missingTargets = new HashSet<string>();
            foreach (var m in historyMessages.Concat(unrespondedMessages))
                if (!string.IsNullOrEmpty(m.ReplyToPlatformMessageId) && !contextIds.Contains(m.ReplyToPlatformMessageId))
                    missingTargets.Add(m.ReplyToPlatformMessageId);
            foreach (var (msg, _) in batch)
                if (!string.IsNullOrEmpty(msg.ReplyTo) && !contextIds.Contains(msg.ReplyTo))
                    missingTargets.Add(msg.ReplyTo);

            // PLACEHOLDER_QUOTED

            if (missingTargets.Count > 0)
                await AppendQuotedContextAsync(sb, missingTargets, contextIds, shortNames, MaxQuoteDepth);

            if (historyMessages.Count > 0)
            {
                sb.AppendLine("<history>");
                foreach (var m in historyMessages)
                {
                    var line = await FormatMessageWithImagesAsync(m, shortNames, contextIds, participants,
                        isNewBlock: false);
                    sb.AppendLine(line);
                }
                sb.AppendLine("</history>");
            }

            sb.AppendLine("<new>");
            foreach (var m in unrespondedMessages)
            {
                var line = await FormatMessageWithImagesAsync(m, shortNames, contextIds, participants,
                    isNewBlock: true);
                sb.AppendLine(line);
            }
            foreach (var (msg, sc) in batch)
            {
                var line = await FormatBatchMessageWithImagesAsync(msg, sc, shortNames, contextIds);
                sb.AppendLine(line);
            }
            sb.Append("</new>");

            return (sb.ToString(), _embeds);
        }

        // PLACEHOLDER_QUOTED_METHOD

        private async Task AppendQuotedContextAsync(StringBuilder sb, HashSet<string> targetIds,
            HashSet<string> contextIds, Dictionary<int, string> shortNames, int maxDepth)
        {
            if (targetIds.Count == 0 || maxDepth <= 0) return;

            var expanded = new List<UserMessage>();
            var nextTargets = new HashSet<string>();

            foreach (var targetId in targetIds)
            {
                if (contextIds.Contains(targetId)) continue;
                try
                {
                    var quoted = await session.GetByPlatformMessageIdAsync(channelId, targetId);
                    if (quoted != null)
                    {
                        var around = await session.GetContextAroundAsync(quoted.Id, channelId, 3);
                        foreach (var m in around)
                        {
                            if (!contextIds.Contains(m.PlatformMessageId ?? ""))
                            {
                                expanded.Add(m);
                                if (!string.IsNullOrEmpty(m.PlatformMessageId))
                                    contextIds.Add(m.PlatformMessageId);
                            }
                        }
                        if (!string.IsNullOrEmpty(quoted.ReplyToPlatformMessageId)
                            && !contextIds.Contains(quoted.ReplyToPlatformMessageId))
                            nextTargets.Add(quoted.ReplyToPlatformMessageId);
                    }
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("ContextBuilder", $"引用上下文查询失败: {ex.Message}");
                }
            }

            if (nextTargets.Count > 0 && maxDepth > 1)
                await AppendQuotedContextAsync(sb, nextTargets, contextIds, shortNames, maxDepth - 1);

            if (expanded.Count > 0)
            {
                sb.AppendLine("<quoted-context>");
                foreach (var m in expanded)
                {
                    var isTarget = targetIds.Contains(m.PlatformMessageId ?? "");
                    var line = await FormatQuotedMessageAsync(m, shortNames, isTarget);
                    sb.AppendLine(line);
                }
                sb.AppendLine("</quoted-context>");
            }
        }

        // PLACEHOLDER_FORMAT_METHODS

        // ── 图片感知的消息格式化 ──

        private async Task<string> FormatMessageWithImagesAsync(UserMessage m,
            Dictionary<int, string> shortNames, HashSet<string> contextIds,
            Dictionary<int, ParticipantInfo>? participants, bool isNewBlock)
        {
            var name = m.IsFromBot ? "Lilara" : ResolveHistoryShortName(m, shortNames);
            var attrs = new StringBuilder();
            if (!string.IsNullOrEmpty(m.PlatformMessageId))
                attrs.Append($" id=\"{SanitizeAttr(m.PlatformMessageId)}\"");
            attrs.Append($" user=\"{SanitizeAttr(name)}\"");
            if (!m.IsFromBot && participants != null && participants.TryGetValue(m.UserId, out var info))
                attrs.Append($" qq=\"{SanitizeAttr(info.PlatformId)}\"");
            if (!string.IsNullOrEmpty(m.ReplyToPlatformMessageId))
                attrs.Append($" reply=\"{SanitizeAttr(m.ReplyToPlatformMessageId)}\"");

            var content = await ResolveImageMarkersAsync(m.Content, m.ImageHashes, isNewBlock);
            return $"<msg{attrs}>{content}</msg>";
        }

        private async Task<string> FormatBatchMessageWithImagesAsync(IncomingMessage msg, SessionContext sc,
            Dictionary<int, string> shortNames, HashSet<string> contextIds)
        {
            var name = shortNames.GetValueOrDefault(sc.User.Id, sc.User.DisplayName);
            if (string.IsNullOrEmpty(name)) name = msg.DisplayName ?? msg.PlatformUserId;
            var attrs = new StringBuilder();
            if (!string.IsNullOrEmpty(msg.PlatformMessageId))
                attrs.Append($" id=\"{SanitizeAttr(msg.PlatformMessageId)}\"");
            attrs.Append($" user=\"{SanitizeAttr(name)}\"");
            attrs.Append($" qq=\"{SanitizeAttr(msg.PlatformUserId)}\"");
            if (!string.IsNullOrEmpty(msg.ReplyTo))
                attrs.Append($" reply=\"{SanitizeAttr(msg.ReplyTo)}\"");
            if (msg.IsMentioned)
                attrs.Append(" mentioned=\"true\"");

            // batch 消息的图片哈希从 attachments 获取
            var hashes = msg.Attachments?
                .Where(a => a.Type == AttachmentType.Image && !string.IsNullOrEmpty(a.Hash))
                .Select(a => a.Hash!)
                .ToList();
            var hashStr = hashes != null && hashes.Count > 0 ? string.Join(",", hashes) : null;

            var content = await ResolveImageMarkersAsync(msg.Content, hashStr, isNewBlock: true);
            return $"<msg{attrs}>{content}</msg>";
        }

        private async Task<string> FormatQuotedMessageAsync(UserMessage m,
            Dictionary<int, string> shortNames, bool isTarget)
        {
            var name = m.IsFromBot ? "Lilara" : ResolveHistoryShortName(m, shortNames);
            var attrs = new StringBuilder();
            if (!string.IsNullOrEmpty(m.PlatformMessageId))
                attrs.Append($" id=\"{SanitizeAttr(m.PlatformMessageId)}\"");
            attrs.Append($" user=\"{SanitizeAttr(name)}\"");
            if (isTarget) attrs.Append(" quoted=\"true\"");
            if (!string.IsNullOrEmpty(m.ReplyToPlatformMessageId))
                attrs.Append($" reply=\"{SanitizeAttr(m.ReplyToPlatformMessageId)}\"");

            // 被引用消息的图片一律直传
            var content = await ResolveImageMarkersAsync(m.Content, m.ImageHashes, isNewBlock: true);
            return $"<msg{attrs}>{content}</msg>";
        }

        // PLACEHOLDER_RESOLVE_IMAGES

        /// <summary>
        /// 将消息内容中的 [IMG:N] 占位符替换为 img 标记。
        /// 根据规则决定直传（加入 embeds）还是仅显示描述。
        /// </summary>
        private async Task<string> ResolveImageMarkersAsync(string? content, string? imageHashes,
            bool isNewBlock)
        {
            if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(imageHashes))
                return SanitizeContent(content);

            var sanitized = SanitizeContent(content);

            if (string.IsNullOrEmpty(imageHashes))
                return sanitized;

            var hashes = imageHashes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(h => h.Trim()).ToList();
            if (hashes.Count == 0) return sanitized;

            // 查询所有图片记录
            var records = new List<ImageRecord?>();
            foreach (var hash in hashes)
                records.Add(await ImageStorage.GetByHashAsync(hash));

            // 检查内容中是否有 [IMG:N] 占位符
            bool hasPlaceholders = ImgPlaceholderRegex.IsMatch(sanitized);

            if (hasPlaceholders)
            {
                // 替换占位符为 <img/> 标记
                sanitized = ImgPlaceholderRegex.Replace(sanitized, match =>
                {
                    var idx = int.Parse(match.Groups[1].Value);
                    if (idx >= records.Count) return "";
                    var record = records[idx];
                    if (record == null) return "";
                    return BuildImgTag(record, isNewBlock);
                });
            }
            else
            {
                // 没有占位符（旧消息），图片标记追加在内容末尾
                var imgTags = new StringBuilder();
                foreach (var record in records)
                {
                    if (record == null) continue;
                    imgTags.Append(BuildImgTag(record, isNewBlock));
                }
                if (imgTags.Length > 0)
                    sanitized += imgTags.ToString();
            }

            return sanitized;
        }

        /// <summary>
        /// 为单张图片生成 img 标记，并决定是否加入直传列表。
        /// </summary>
        private string BuildImgTag(ImageRecord record, bool isNewBlock)
        {
            var id = record.Id;
            var desc = record.Description;
            var category = record.Category;
            var isSticker = category == "sticker";
            var ocrText = record.OcrText;

            // 规则：表情包 → 分类占位符（不需要描述）
            if (isSticker)
                return $"<img id=\"{id}\" type=\"sticker\"/>";

            // 规则：有丰富 OCR 文本 → 用 OCR 摘要
            if (!string.IsNullOrEmpty(ocrText) && ocrText.Length >= 20)
            {
                var textPreview = ocrText.Length > 100 ? ocrText[..100] + "..." : ocrText;
                return $"<img id=\"{id}\" type=\"text\" text=\"{SanitizeAttr(textPreview)}\"/>";
            }

            // 规则：有实质描述（非空字符串）→ 用描述
            if (!string.IsNullOrEmpty(desc))
            {
                if (!isNewBlock)
                    return $"<img id=\"{id}\" desc=\"{SanitizeAttr(desc)}\"/>";
            }

            // 需要直传：检查大小限制
            var filePath = GetEmbedPath(record);
            if (filePath == null)
                return !string.IsNullOrEmpty(desc)
                    ? $"<img id=\"{id}\" desc=\"{SanitizeAttr(desc)}\"/>"
                    : $"<img id=\"{id}\" unavailable=\"true\"/>";

            var fileSize = GetFileSize(filePath);

            // 单张超限
            if (fileSize > ImageStorage.MaxDirectSendSize)
            {
                return !string.IsNullOrEmpty(desc)
                    ? $"<img id=\"{id}\" desc=\"{SanitizeAttr(desc)}\" oversized=\"true\"/>"
                    : $"<img id=\"{id}\" oversized=\"true\"/>";
            }

            // 总量超限或数量超限
            if (_embeds.Count >= ImageStorage.MaxDirectSendCount ||
                _totalEmbedSize + fileSize > ImageStorage.MaxTotalDirectSendSize)
            {
                return !string.IsNullOrEmpty(desc)
                    ? $"<img id=\"{id}\" desc=\"{SanitizeAttr(desc)}\"/>"
                    : $"<img id=\"{id}\" pending=\"true\"/>";
            }

            // 加入直传列表
            _embeds.Add(new ImageEmbed { ImageId = id, Path = filePath });
            _totalEmbedSize += fileSize;
            return $"<img id=\"{id}\"/>";
        }

        private static string? GetEmbedPath(ImageRecord record)
        {
            // 优先缩略图
            if (!string.IsNullOrEmpty(record.ThumbnailPath) && File.Exists(record.ThumbnailPath))
                return record.ThumbnailPath;
            if (File.Exists(record.LocalPath))
                return record.LocalPath;
            return null;
        }

        private static long GetFileSize(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        // ── 静态格式化方法（兼容旧调用） ──

        public static string FormatDbMessage(UserMessage m, Dictionary<int, string> shortNames,
            HashSet<string> contextIds, Dictionary<int, ParticipantInfo>? participants = null)
        {
            var name = m.IsFromBot ? "Lilara" : ResolveHistoryShortName(m, shortNames);
            var attrs = new StringBuilder();
            if (!string.IsNullOrEmpty(m.PlatformMessageId))
                attrs.Append($" id=\"{SanitizeAttr(m.PlatformMessageId)}\"");
            attrs.Append($" user=\"{SanitizeAttr(name)}\"");
            if (!m.IsFromBot && participants != null && participants.TryGetValue(m.UserId, out var info))
                attrs.Append($" qq=\"{SanitizeAttr(info.PlatformId)}\"");
            if (!string.IsNullOrEmpty(m.ReplyToPlatformMessageId))
                attrs.Append($" reply=\"{SanitizeAttr(m.ReplyToPlatformMessageId)}\"");
            if (m.ImageCount > 0)
                attrs.Append($" images=\"{m.ImageCount}\"");
            return $"<msg{attrs}>{SanitizeContent(m.Content)}</msg>";
        }

        public static string FormatBatchMessage(IncomingMessage msg, SessionContext sc,
            Dictionary<int, string> shortNames, HashSet<string> contextIds)
        {
            var name = shortNames.GetValueOrDefault(sc.User.Id, sc.User.DisplayName);
            if (string.IsNullOrEmpty(name)) name = msg.DisplayName ?? msg.PlatformUserId;
            var attrs = new StringBuilder();
            if (!string.IsNullOrEmpty(msg.PlatformMessageId))
                attrs.Append($" id=\"{SanitizeAttr(msg.PlatformMessageId)}\"");
            attrs.Append($" user=\"{SanitizeAttr(name)}\"");
            attrs.Append($" qq=\"{SanitizeAttr(msg.PlatformUserId)}\"");
            if (!string.IsNullOrEmpty(msg.ReplyTo))
                attrs.Append($" reply=\"{SanitizeAttr(msg.ReplyTo)}\"");
            if (msg.IsMentioned)
                attrs.Append(" mentioned=\"true\"");
            var imgCount = msg.Attachments?.Count(a => a.Type == AttachmentType.Image) ?? 0;
            if (imgCount > 0)
                attrs.Append($" images=\"{imgCount}\"");
            return $"<msg{attrs}>{SanitizeContent(msg.Content)}</msg>";
        }

        // ── 辅助方法 ──

        public static Dictionary<int, string> ResolveShortNames(Dictionary<int, ParticipantInfo> participants)
        {
            var result = new Dictionary<int, string>();
            var groups = participants.GroupBy(p => p.Value.DisplayName, StringComparer.Ordinal);

            foreach (var group in groups)
            {
                var members = group.ToList();
                if (members.Count == 1)
                {
                    result[members[0].Key] = members[0].Value.DisplayName;
                }
                else
                {
                    var nicknames = members.Select(m => m.Value.Nickname).ToList();
                    bool nicknamesUnique = nicknames.Distinct().Count() == nicknames.Count
                                           && nicknames.All(n => !string.IsNullOrEmpty(n));
                    foreach (var member in members)
                    {
                        if (nicknamesUnique && !string.IsNullOrEmpty(member.Value.Nickname))
                            result[member.Key] = $"{member.Value.DisplayName}({member.Value.Nickname})";
                        else
                        {
                            var pid = member.Value.PlatformId;
                            var suffix = pid.Length > 4 ? pid[^4..] : pid;
                            result[member.Key] = $"{member.Value.DisplayName}(…{suffix})";
                        }
                    }
                }
            }
            return result;
        }

        public static string ResolveHistoryShortName(UserMessage m, Dictionary<int, string> shortNames)
        {
            if (shortNames.TryGetValue(m.UserId, out var name))
                return name;
            return !string.IsNullOrEmpty(m.SenderName) ? m.SenderName : "用户";
        }

        public static string TrustLevelToRelation(TrustLevel level) => level switch
        {
            TrustLevel.Hostile => "不太想理",
            TrustLevel.Wary => "有点警惕",
            TrustLevel.Unknown => "陌生人",
            TrustLevel.Stranger => "不太熟",
            TrustLevel.Understanding => "认识",
            TrustLevel.Familiarity => "熟人",
            TrustLevel.Trust => "好友",
            TrustLevel.AbsoluteTrust => "挚友",
            _ => "陌生人"
        };

        public static string SanitizeAttr(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var s = value.Replace("\n", " ").Replace("\r", "").Replace("\"", "'");
            s = s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            return s.Length > 40 ? s[..40] : s;
        }

        public static string SanitizeContent(string? content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            return content.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}