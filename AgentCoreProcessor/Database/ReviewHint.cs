using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 复盘标记。ChannelEngine 工作时记录"值得复盘关注"的内容，
    /// ReviewEngine 启动时消费这些标记作为方向参考。
    /// </summary>
    [Table("ReviewHints")]
    internal class ReviewHint
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        public string Content { get; set; } = "";

        /// <summary>标记位置（消息 ID）</summary>
        public int? MessageId { get; set; }

        public int? PersonId { get; set; }
        public int? ChannelId { get; set; }

        /// <summary>model = 工作端标记, framework = 自动生成</summary>
        public string Source { get; set; } = "model";

        public bool IsProcessed { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
