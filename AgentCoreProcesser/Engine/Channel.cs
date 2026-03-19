using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcesser.Engine
{
    [Table ("Channels")]
    internal class Channel
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }// 频道ID，唯一标识一个频道
        public required string Name { get; set; }// 频道名称
    }
}
