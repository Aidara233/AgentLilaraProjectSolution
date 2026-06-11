global using SleepState = AgentLilara.PluginSDK.SleepState;

using System;
using System.Linq;

namespace AgentCoreProcessor.Engine
{
    internal static class SleepUtils
    {
        public static readonly string[] WakeKeywords =
            ["起床", "醒醒", "wake", "起来", "叫醒", "别睡了", "醒来", "!", "！", "—", "——"];

        public static bool ContainsWakeKeyword(string content)
        {
            var lower = content.ToLowerInvariant();
            return WakeKeywords.Any(k => lower.Contains(k));
        }

        public static int EstimateTokens(string content)
        {
            if (string.IsNullOrEmpty(content)) return 0;
            int cjk = content.Count(c => c > 0x2E80);
            int nonCjk = content.Length - cjk;
            int englishWords = 0;
            if (nonCjk > 0)
            {
                var latinOnly = new string(content.Where(c => c <= 0x2E80).ToArray());
                englishWords = latinOnly.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
            }
            return cjk * 2 + englishWords * 2;
        }
    }
}
