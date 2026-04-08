using System;
using SQLite;

namespace AgentCoreProcesser.Engine
{
    [Table("Topics")]
    internal class Topic
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public int ChannelId { get; set; }
        public required string Name { get; set; }
        public DateTime LastMessageTime { get; set; }
    }
}
