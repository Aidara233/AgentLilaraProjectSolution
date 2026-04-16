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

        /// <summary>摘要的向量表示，用于语义话题分类</summary>
        public byte[]? Embedding { get; set; }

        /// <summary>话题内消息计数，用于触发摘要更新</summary>
        public int MessageCount { get; set; } = 0;

        /// <summary>是否活跃（超时自动关闭，避免 Topic 无限增长）</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>是否为频道的闲聊兜底话题（不过期、不需要精确摘要）</summary>
        public bool IsChatTopic { get; set; } = false;

        /// <summary>是否为频道的未分类话题（实时阶段消息暂存，做梦时归档）</summary>
        public bool IsUnclassified { get; set; } = false;

        /// <summary>最后一条消息的时间</summary>
        public DateTime LastMessageTime { get; set; }
    }
}
