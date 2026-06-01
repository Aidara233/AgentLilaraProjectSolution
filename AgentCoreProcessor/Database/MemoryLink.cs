using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 记忆关联实体。双轴边模型：Relevance（关联性）+ Support（支持度，负值=矛盾）。
    /// </summary>
    [Table("MemoryLinks")]
    internal class MemoryLink
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>源记忆ID</summary>
        public int SourceId { get; set; }

        /// <summary>目标记忆ID</summary>
        public int TargetId { get; set; }

        /// <summary>关联性 (0.0-1.0)。两条记忆在说同一件事的程度。</summary>
        [Column("Strength")]
        public float Relevance { get; set; }

        /// <summary>支持度 (-1.0~1.0)。正数=相互支持，负数=矛盾，零=无关。用于确定性传播方向。</summary>
        public float Support { get; set; } = 1.0f;

        /// <summary>关联类型（共现/时序/语义/因果等）</summary>
        public string LinkType { get; set; } = "";

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>最后更新时间</summary>
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
