using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 话题实体，用于将频道内的消息按话题归类，防止上下文污染。
    /// </summary>
    [Table("Topics")]
    internal class Topic
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>所属频道ID</summary>
        public int ChannelId { get; set; }

        /// <summary>话题名称</summary>
        public string Name { get; set; } = "";

        /// <summary>话题摘要，用于后续消息匹配和上下文注入</summary>
        public string Summary { get; set; } = "";

        /// <summary>是否活跃（超时自动关闭，避免 Topic 无限增长）</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>最后一条消息的时间</summary>
        public DateTime LastMessageTime { get; set; }
    }
}
