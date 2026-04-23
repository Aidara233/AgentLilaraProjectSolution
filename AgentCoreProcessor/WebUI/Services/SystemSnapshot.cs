using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class SystemSnapshot
    {
        public bool IsIdle { get; init; }
        public TimeSpan IdleDuration { get; init; }
        public DateTime LastMessageTime { get; init; }
        public bool MuteMode { get; init; }
        public List<(string Type, int Count)> EngineSummary { get; init; } = new();
        public List<WorkerSnapshot> Workers { get; init; } = new();
        public DreamStateSnapshot? DreamState { get; init; }
        public DateTime SnapshotTime { get; init; } = DateTime.Now;
    }
}
