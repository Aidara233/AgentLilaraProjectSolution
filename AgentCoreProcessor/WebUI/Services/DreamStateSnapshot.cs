using System;
using System.Collections.Generic;
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
        public string? CurrentInputDescription { get; init; }

        // 上一个完成的片段
        public string? LastFragmentType { get; init; }
        public string? LastFragmentSummary { get; init; }
        public List<FragmentDetailSnapshot>? LastFragmentDetails { get; init; }
    }

    internal class FragmentDetailSnapshot
    {
        public string Action { get; init; } = "";
        public int? MemoryId { get; init; }
        public string? OldValue { get; init; }
        public string? NewValue { get; init; }
        public string? Note { get; init; }
    }
}
