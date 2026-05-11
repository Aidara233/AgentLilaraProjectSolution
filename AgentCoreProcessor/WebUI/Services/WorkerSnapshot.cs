using System;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class WorkerSnapshot
    {
        public int ChannelId { get; init; }
        public string? ChannelName { get; init; }
        public bool IsAlive { get; init; }
        public bool IsBusy { get; init; }
        public bool IsWorkingMode { get; init; }
        public bool IsInWorkingSession { get; init; }
        public float Impulse { get; init; }
        public float Threshold { get; init; }
        public float ChannelAffinity { get; init; }
        public string Importance { get; init; } = "normal";
        public int ActiveExtractionThreshold { get; init; }
        public int LurkingExtractionThreshold { get; init; }
        public int LastExtractedMessageId { get; init; }
        public int LatestMessageId { get; init; }
        public bool ExtractionRunning { get; init; }
        public bool AutoExtractionEnabled { get; init; }
        public int UnrespondedMessageCount { get; init; }
        public int ConsecutiveExternalTriggers { get; init; }
        public DateTime? LastCompletionTime { get; init; }
        public int TotalRounds { get; init; }
        public int SilentRounds { get; init; }
        public int AuthorizedToolCount { get; init; }
        public int ParticipantCount { get; init; }
        public int ProcessedMessageCount { get; init; }

        // 错误状态
        public int ConsecutiveFailures { get; init; }
        public int TotalErrorCount { get; init; }
        public DateTime? LastErrorTime { get; init; }
        public string? LastErrorMessage { get; init; }

        public bool HasRecentError => LastErrorTime.HasValue
            && (DateTime.Now - LastErrorTime.Value).TotalMinutes < 10;
    }
}
