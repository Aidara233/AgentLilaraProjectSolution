using SQLite;
using System;

namespace AgentCoreProcessor.Engine
{
    [Table("Memories")]
    internal class Memory
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }// 记忆ID，唯一标识一个记忆
        public required string Content { get; set; }
        public DateTime Time { get; set; }
        public int UserId { get; set; }
        public int TopicId { get; set; }
        public int ChannelId { get; set; }
    }
}
