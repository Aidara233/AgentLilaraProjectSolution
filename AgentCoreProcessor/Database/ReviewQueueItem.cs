using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 待验证队列项。所有需要验证的来源（冲突组超预算 / Dream 发现 / 手动标记 / 框架事件）
    /// 统一入此队列，作为 Review 的唯一工作源（见 docs/memory-redesign/05 6.0）。
    /// </summary>
    [Table("ReviewQueueItems")]
    internal class ReviewQueueItem
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>目标引用（断言Id/消息Id/冲突组Id/JournalId，存字符串）</summary>
        public string TargetRef { get; set; } = "";

        /// <summary>目标类型："Claim" | "Message" | "ConflictGroup" | "Journal"</summary>
        public string TargetKind { get; set; } = "Claim";

        /// <summary>为什么进队列（排他冲突/悬空推断/手动标记/支持链矛盾）</summary>
        public string? Reason { get; set; }

        /// <summary>入队来源："Conflict" | "Dream" | "Manual" | "Framework"</summary>
        public string Source { get; set; } = "Framework";

        /// <summary>优先级："High" | "Normal" | "Low"</summary>
        public string Priority { get; set; } = "Normal";

        /// <summary>状态："Pending" | "UnderReview" | "Done" | "Dropped"</summary>
        public string Status { get; set; } = "Pending";

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>认领时间（开始处理）</summary>
        public DateTime? ClaimedAt { get; set; }

        /// <summary>完成时间</summary>
        public DateTime? DoneAt { get; set; }

        /// <summary>中断时间（崩溃恢复标记）</summary>
        public DateTime? InterruptedAt { get; set; }

        /// <summary>处理结果关联（可选）</summary>
        public string? ResultRef { get; set; }
    }
}
