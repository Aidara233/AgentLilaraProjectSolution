using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 情绪快照实体。五轴情绪模型，每轴范围 [-1, 1]（不强制，由调用方保证）。
    /// </summary>
    [Table("EmotionSnapshots")]
    internal class EmotionSnapshot
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>兴奋（-1 到 1）</summary>
        public float Excitement { get; set; } = 0;

        /// <summary>喜悦（-1 到 1）</summary>
        public float Joy { get; set; } = 0;

        /// <summary>敏觉（-1 到 1）</summary>
        public float Alertness { get; set; } = 0;

        /// <summary>满足（-1 到 1）</summary>
        public float Satisfaction { get; set; } = 0;

        /// <summary>积极（-1 到 1）</summary>
        public float Optimism { get; set; } = 0;

        /// <summary>触发来源 Journal/L1 引用 JSON</summary>
        public string? SourceRef { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
