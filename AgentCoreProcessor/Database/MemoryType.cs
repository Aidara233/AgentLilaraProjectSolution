namespace AgentCoreProcessor.Database
{
    internal static class MemoryType
    {
        public const string Knowledge = "knowledge";
        public const string Fact = "fact";
        public const string Feedback = "feedback";
        public const string Inference = "inference";
        public const string Event = "event";
        public const string State = "state";
        public const string Preference = "preference";

        /// <summary>计算记忆标签匹配分。person/channel 匹配各+1，Knowledge 类型+1。</summary>
        public static int GetMatchScore(int? entryPersonId, int? entryChannelId, string entryType,
            int? targetPersonId, int? targetChannelId)
        {
            int matchCount = 0;
            if (entryPersonId != null && entryPersonId == targetPersonId) matchCount++;
            if (entryChannelId != null && entryChannelId == targetChannelId) matchCount++;
            if (entryType == Knowledge) matchCount++;
            return matchCount;
        }
    }
}
