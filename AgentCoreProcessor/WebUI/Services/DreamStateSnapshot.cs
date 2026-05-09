using System;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class DreamStateSnapshot
    {
        public bool ForceFlag { get; init; }
        public DateTime? LastDaydreamTime { get; init; }
        public SleepLevel PendingLevel { get; init; }
        public bool HasActiveDream { get; init; }

        // 实时进度
        public string? CurrentFragment { get; init; }
        public int FragmentsCompleted { get; init; }
        public int FragmentsTotal { get; init; }
        public DateTime? CurrentFragmentStartTime { get; init; }
    }
}
