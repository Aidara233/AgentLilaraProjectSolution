using System;

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
    }
}
