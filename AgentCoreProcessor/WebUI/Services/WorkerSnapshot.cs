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
        public float MessageRate { get; init; }
        public float Expectation { get; init; }
        public float Reality { get; init; }
        public float ChannelAffinity { get; init; }
        public string Importance { get; init; } = "normal";
        public int ExtractionInterval { get; init; }
        public int UnrespondedMessageCount { get; init; }
        public int ConsecutiveExternalTriggers { get; init; }
        public DateTime? LastCompletionTime { get; init; }
        public int TotalRounds { get; init; }
        public int SilentRounds { get; init; }
        public int AuthorizedToolCount { get; init; }
        public int ParticipantCount { get; init; }
        public int ProcessedMessageCount { get; init; }
    }
}
