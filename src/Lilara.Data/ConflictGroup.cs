using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 冲突组实体。一组同（subject + 排他属性）槽位的互相冲突断言，
    /// 组内全部成员可信度（c_actual）之和 ≤ 1（信念预算瓜分）。
    /// 遵循《memory-redesign/05-credibility-conflict-model.md》第五节设计。
    /// </summary>
    [Table("ConflictGroups")]
    internal class ConflictGroup
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>冲突槽位 subject（表面词），如"小明"</summary>
        public string SubjectKey { get; set; } = "";

        /// <summary>冲突槽位排他属性名（IsExclusive 关键词条目），如"颜色"</summary>
        public string PredicateKey { get; set; } = "";

        /// <summary>检测原因</summary>
        public string? Reason { get; set; }

        /// <summary>检测到冲突的时间</summary>
        public DateTime DetectedAt { get; set; } = DateTime.Now;

        /// <summary>解决时间（null = 未解决）</summary>
        public DateTime? ResolvedAt { get; set; }

        /// <summary>解决者标识（session/命令/工具名等）</summary>
        public string? ResolvedBy { get; set; }
    }
}
