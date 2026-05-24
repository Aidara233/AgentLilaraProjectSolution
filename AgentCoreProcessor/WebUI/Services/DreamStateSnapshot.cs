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

        // 实时进度（兼容旧字段，并行时 CurrentFragment 取第一个运行中片段）
        public string? CurrentFragment { get; init; }
        public int FragmentsCompleted { get; init; }
        public int FragmentsTotal { get; init; }
        public DateTime? CurrentFragmentStartTime { get; init; }
        public string? CurrentInputDescription { get; init; }

        // 上一个完成的片段
        public string? LastFragmentType { get; init; }
        public string? LastFragmentSummary { get; init; }
        public List<FragmentDetailSnapshot>? LastFragmentDetails { get; init; }

        // 活跃做梦时内存中所有已完成的片段（未入库）
        public List<FragmentRecord>? CompletedFragments { get; init; }

        // 资源与预算（并行化新增）
        public int AvailableResources { get; init; }
        public int TotalResources { get; init; }
        public int TokensUsed { get; init; }
        public int MainBudget { get; init; }
        public int ReserveBudget { get; init; }
        public int TodoCount { get; init; }
        public int RunningCount { get; init; }
        public bool BudgetExhausted { get; init; }

        // 运行中片段列表
        public List<RunningFragmentSnapshot>? RunningFragments { get; init; }
    }

    internal class FragmentDetailSnapshot
    {
        public string Action { get; init; } = "";
        public int? MemoryId { get; init; }
        public string? OldValue { get; init; }
        public string? NewValue { get; init; }
        public string? Note { get; init; }
    }

    internal class RunningFragmentSnapshot
    {
        public string Type { get; init; } = "";
        public int ResourceCost { get; init; }
    }
}
