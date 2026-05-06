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
    }
}
