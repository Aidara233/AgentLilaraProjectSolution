using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    [Table("Beacons")]
    internal class Beacon
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        public string Content { get; set; } = "";

        /// <summary>标记位置（消息 ID）</summary>
        public int? MessageId { get; set; }

        public int? PersonId { get; set; }
        public int? ChannelId { get; set; }

        /// <summary>创建来源：model / framework / engine:xxx</summary>
        public string Source { get; set; } = "model";

        /// <summary>消费者标识：review / memory_consolidation / trust_eval 等</summary>
        public string Consumer { get; set; } = "review";

        public bool IsProcessed { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? ProcessedAt { get; set; }
    }
}
