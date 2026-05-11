using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// XML 格式上下文构建器。从频道消息历史构建模型输入的 XML 上下文。
    /// </summary>
    internal class ContextBuilder
    {
        private readonly SessionManager session;
        private readonly int channelId;

        private const int MaxQuoteDepth = 2;

        public ContextBuilder(SessionManager session, int channelId)
        {
            this.session = session;
            this.channelId = channelId;
        }

        public async Task<(string Xml, List<string> QuotedImagePaths)> BuildContextXmlAsync(
            List<(IncomingMessage Message, SessionContext Context)> batch,
            List<UserMessage> recentMessages,
            Dictionary<int, ParticipantInfo> participants)
        {
            var sb = new StringBuilder();
            var quotedImagePaths = new List<string>();
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

            if (missingTargets.Count > 0)
                await AppendQuotedContextAsync(sb, missingTargets, contextIds, shortNames, MaxQuoteDepth, quotedImagePaths);

            if (historyMessages.Count > 0)
            {
                sb.AppendLine("<history>");
                foreach (var m in historyMessages)
                    sb.AppendLine(FormatDbMessage(m, shortNames, contextIds, participants));
                sb.AppendLine("</history>");
            }

            sb.AppendLine("<new>");
            foreach (var m in unrespondedMessages)
                sb.AppendLine(FormatDbMessage(m, shortNames, contextIds, participants));
            foreach (var (msg, sc) in batch)
                sb.AppendLine(FormatBatchMessage(msg, sc, shortNames, contextIds));
            sb.Append("</new>");

            return (sb.ToString(), quotedImagePaths);
        }

        private async Task AppendQuotedContextAsync(StringBuilder sb, HashSet<string> targetIds,
            HashSet<string> contextIds, Dictionary<int, string> shortNames, int maxDepth,
            List<string> quotedImagePaths)
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
                catch (System.Exception ex)
                {
                    FrameworkLogger.Log("ContextBuilder", $"引用上下文查询失败: {ex.Message}");
                }
            }

            if (nextTargets.Count > 0 && maxDepth > 1)
                await AppendQuotedContextAsync(sb, nextTargets, contextIds, shortNames, maxDepth - 1, quotedImagePaths);

            if (expanded.Count > 0)
            {
                foreach (var m in expanded)
                {
                    if (!string.IsNullOrEmpty(m.ImageHashes))
                    {
                        var paths = await ImageStorage.ResolvePathsAsync(m.ImageHashes);
                        quotedImagePaths.AddRange(paths);
                    }
                }

                sb.AppendLine("<quoted-context>");
                foreach (var m in expanded)
                {
                    var isTarget = targetIds.Contains(m.PlatformMessageId ?? "");
                    var quotedAttr = isTarget ? " quoted=\"true\"" : "";
                    var name = m.IsFromBot ? "Lilara" : ResolveHistoryShortName(m, shortNames);
                    var attrs = new StringBuilder();
                    if (!string.IsNullOrEmpty(m.PlatformMessageId))
                        attrs.Append($" id=\"{SanitizeAttr(m.PlatformMessageId)}\"");
                    attrs.Append($" user=\"{SanitizeAttr(name)}\"");
                    attrs.Append(quotedAttr);
                    if (!string.IsNullOrEmpty(m.ReplyToPlatformMessageId))
                        attrs.Append($" reply=\"{SanitizeAttr(m.ReplyToPlatformMessageId)}\"");
                    if (m.ImageCount > 0)
                        attrs.Append($" images=\"{m.ImageCount}\"");
                    sb.AppendLine($"<msg{attrs}>{SanitizeContent(m.Content)}</msg>");
                }
                sb.AppendLine("</quoted-context>");
            }
        }

        // ---- 格式化方法 ----

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

        // ---- 辅助方法 ----

        public static Dictionary<int, string> ResolveShortNames(Dictionary<int, ParticipantInfo> participants)
        {
            var result = new Dictionary<int, string>();
            var groups = participants.GroupBy(p => p.Value.DisplayName, System.StringComparer.Ordinal);

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
