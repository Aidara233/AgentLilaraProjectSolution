using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 解析模型输出中的 &lt;at/&gt; 和 &lt;reply/&gt; 标签。
    /// </summary>
    internal static class BotOutputParser
    {
        private static readonly Regex AtTagRegex =
            new(@"<at\s+user=""([^""]+)""\s*/>", RegexOptions.Compiled);
        private static readonly Regex ReplyTagRegex =
            new(@"<reply\s+id=""([^""]+)""\s*/>", RegexOptions.Compiled);

        public static (string Content, string? ReplyTo, List<string>? Mentions) Parse(
            string raw, Dictionary<int, ParticipantInfo> participants)
        {
            string? replyTo = null;
            List<string>? mentions = null;

            var replyMatch = ReplyTagRegex.Match(raw);
            if (replyMatch.Success)
            {
                replyTo = replyMatch.Groups[1].Value;
                raw = raw.Remove(replyMatch.Index, replyMatch.Length).TrimStart();
            }

            var nameToQq = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var (_, info) in participants)
            {
                nameToQq.TryAdd(info.DisplayName, info.PlatformId);
                if (!string.IsNullOrEmpty(info.Nickname))
                    nameToQq.TryAdd(info.Nickname, info.PlatformId);
            }

            raw = AtTagRegex.Replace(raw, match =>
            {
                var userName = match.Groups[1].Value;
                if (nameToQq.TryGetValue(userName, out var qq))
                {
                    mentions ??= new List<string>();
                    if (!mentions.Contains(qq)) mentions.Add(qq);
                    return "";
                }
                return $"@{userName} ";
            });

            return (raw.Trim(), replyTo, mentions);
        }
    }
}
