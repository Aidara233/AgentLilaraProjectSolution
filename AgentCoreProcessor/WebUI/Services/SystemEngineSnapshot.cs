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
    }

    internal class SubAgentInfo
    {
        public string SessionId { get; init; } = "";
        public string Type { get; init; } = "";
        public bool IsAlive { get; init; }
    }
}
