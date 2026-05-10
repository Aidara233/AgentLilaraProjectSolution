using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    [Table("DreamSessions")]
    internal class DreamSession
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        public string Level { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int FragmentsExecuted { get; set; }
        public bool WasInterrupted { get; set; }
    }

    [Table("DreamFragments")]
    internal class DreamFragment
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int SessionId { get; set; }

        public string Type { get; set; } = "";
        public int SeqIndex { get; set; }
        public DateTime StartTime { get; set; }
        public double DurationSeconds { get; set; }
        public bool Success { get; set; }
        public string Summary { get; set; } = "";

        /// <summary>输入记忆 ID 列表（逗号分隔）。Link 格式: "target:id;candidates:id1,id2,..."</summary>
        public string? InputMemoryIds { get; set; }

        /// <summary>模型原始输出（JSON）</summary>
        public string? OutputRaw { get; set; }
    }

    [Table("DreamFragmentDetails")]
    internal class DreamFragmentDetail
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int FragmentId { get; set; }

        public string Action { get; set; } = "";
        public int? MemoryId { get; set; }
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
        public string? Note { get; set; }
    }
}
