using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 主记忆实体。多维标签模型，支持按 Person/Channel/Topic 组合检索。
    /// 同时预留做梦机制所需的元数据字段。
    /// </summary>
    [Table("Memories")]
    internal class MemoryEntry
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>关联的自然人（软匹配加分，不做硬过滤）</summary>
        public int? PersonId { get; set; }

        /// <summary>关联的频道（软匹配加分，不做硬过滤）</summary>
        public int? ChannelId { get; set; }

        /// <summary>记忆类型：knowledge/fact/feedback/inference/event</summary>
        public string Type { get; set; } = MemoryType.Fact;

        /// <summary>记忆主题（如"Kimi 1.5"、"小明"、"rope扩展"），用于主题匹配</summary>
        public string? Subject { get; set; }

        /// <summary>记忆内容</summary>
        public string Content { get; set; } = "";

        /// <summary>向量嵌入（float[] 序列化为 byte[]，SQLite 存 BLOB）</summary>
        public byte[]? Embedding { get; set; }

        /// <summary>重要性 (0.0-1.0)，做梦时调整</summary>
        public float Importance { get; set; } = 0.5f;

        /// <summary>置信度：high/low，从临时记忆继承，被用户确认后升为 high</summary>
        public string Confidence { get; set; } = "high";

        /// <summary>反馈标记：null=无反馈, positive=被用户肯定, negative=被用户否定</summary>
        public string? Feedback { get; set; }

        // ---- 来源追溯 ----

        /// <summary>普通记忆的来源消息ID（与 SourceMemoryIds 互斥）</summary>
        public int? SourceMessageId { get; set; }

        /// <summary>衍生记忆的来源记忆ID列表（JSON 格式，与 SourceMessageId 互斥）</summary>
        public string? SourceMemoryIds { get; set; }

        // ---- 做梦元数据 ----

        /// <summary>是否为做梦产生的衍生记忆</summary>
        public bool IsDerived { get; set; } = false;

        /// <summary>衍生记忆的来源哈希，防重复生成</summary>
        public string? SourceHash { get; set; }

        /// <summary>上次被做梦处理的时间</summary>
        public DateTime? LastDreamTime { get; set; }

        // ---- 生命周期 ----

        /// <summary>是否为永久记忆</summary>
        public bool IsPersistent { get; set; } = true;

        /// <summary>临时记忆的过期时间</summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>最后访问时间</summary>
        public DateTime LastAccessedAt { get; set; } = DateTime.Now;
    }
}
