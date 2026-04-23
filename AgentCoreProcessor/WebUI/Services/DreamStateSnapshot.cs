using System;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class DreamStateSnapshot
    {
        public float ScoreOffset { get; init; }
        public bool CustomRedAlert { get; init; }
        public bool ForceFlag { get; init; }
        public bool DreamPermission { get; init; }
        public DateTime? PermissionRequestTime { get; init; }
        public DateTime? LastDaydreamTime { get; init; }
        public DateTime? LastDeepSleepTime { get; init; }
        public SleepLevel PendingLevel { get; init; }
        public bool HasActiveDream { get; init; }
    }
}
