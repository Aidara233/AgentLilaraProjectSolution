using SQLite;
using System;

namespace AgentCoreProcessor.Database
{
    [Table ("Channels")]
    internal class Channel
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }// 频道ID，唯一标识一个频道
        public string Name { get; set; } = "";// 频道名称
    }
}
