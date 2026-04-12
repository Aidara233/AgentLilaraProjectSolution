using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 记忆关联实体。存储记忆之间的关系（时间共现、行为序列等）。
    /// 属于主记忆库的一部分，做梦时由"关联重建"片段维护。
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

        /// <summary>关联强度 (0.0-1.0)</summary>
        public float Strength { get; set; }

        /// <summary>关联类型（共现/时序/语义/因果等）</summary>
        public string LinkType { get; set; } = "";

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>最后更新时间</summary>
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
