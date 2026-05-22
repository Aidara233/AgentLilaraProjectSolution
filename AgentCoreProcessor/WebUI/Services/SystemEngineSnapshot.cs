using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class SystemEngineSnapshot
    {
        public bool IsAlive { get; init; }
        public int TaskQueueDepth { get; init; }
        public int ActiveSubAgentCount { get; init; }
        public bool HasPendingSleepRequest { get; init; }
        public string? SleepRequestId { get; init; }
        public float? SleepScore { get; init; }
        public DateTime? SleepRequestTime { get; init; }
        public DateTime LastHealthCheck { get; init; }
        public List<SubAgentInfo> SubAgents { get; init; } = new();
        public Dictionary<string, string> PinboardEntries { get; init; } = new();
        public Dictionary<string, string> ThinkingNotes { get; init; } = new();
        public int ContextRoundCount { get; init; }
        public bool HasContextSummary { get; init; }

        // 错误状态
        public int ConsecutiveFailures { get; init; }
        public int TotalErrorCount { get; init; }
        public DateTime? LastErrorTime { get; init; }
        public string? LastErrorMessage { get; init; }
        public int RestartCount { get; init; }
        public DateTime? LastDeathTime { get; init; }

        public bool HasRecentError => LastErrorTime.HasValue
            && (DateTime.Now - LastErrorTime.Value).TotalMinutes < 10;
    }

    internal class SubAgentInfo
    {
        public string SessionId { get; init; } = "";
        public string Type { get; init; } = "";
        public bool IsAlive { get; init; }
        public string? CurrentInstruction { get; init; }
        public string? LastResult { get; init; }
        public string? DelegationId { get; init; }
    }
}
