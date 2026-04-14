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
        public float Affinity { get; set; } = 1.0f;// 频道亲和度，影响冲动值增益
    }
}
