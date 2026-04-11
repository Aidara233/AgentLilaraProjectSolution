using SQLite;
using System;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 记忆的作用域类型。
    /// </summary>
    public enum MemoryScope
    {
        Global = 0,   // 跨所有频道和用户的知识
        User = 1,     // 绑定到具体用户
        Channel = 2,  // 绑定到频道
        Topic = 3     // 绑定到具体话题
    }

    /// <summary>
    /// 记忆实体，存储 Lilara 对用户、频道、话题的认知。
    /// 按作用域（Global/User/Channel/Topic）和生命周期（永久/临时）分类。
    /// </summary>
    [Table("Memories")]
    internal class MemoryEntry
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>作用域类型</summary>
        public MemoryScope Scope { get; set; }

        /// <summary>作用域对应的 ID（userId/channelId/topicId，Global 时为 0）</summary>
        public int ScopeId { get; set; }

        /// <summary>记忆内容</summary>
        public string Content { get; set; } = "";

        /// <summary>向量嵌入（暂存为字符串，后续接入向量模型时改为 byte[]）</summary>
        public string? Embedding { get; set; }

        /// <summary>是否为永久记忆（false 则为临时，有过期时间）</summary>
        public bool IsPersistent { get; set; }

        /// <summary>临时记忆的过期时间（永久记忆为 null）</summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>最后访问时间，用于 LRU 淘汰或权重衰减</summary>
        public DateTime LastAccessedAt { get; set; } = DateTime.Now;
    }
}
