using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 主记忆实体。双轴模型：Importance（重要性，随时间衰减）+ Certainty（确定性，仅证据可改变）。
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

        /// <summary>记忆类型：knowledge/fact/feedback/inference/event/state/preference</summary>
        public string Type { get; set; } = MemoryType.Fact;

        /// <summary>记忆主题（如"Kimi 1.5"、"小明"、"rope扩展"），用于主题匹配</summary>
        public string? Subject { get; set; }

        /// <summary>记忆内容</summary>
        public string Content { get; set; } = "";

        /// <summary>向量嵌入（float[] 序列化为 byte[]，SQLite 存 BLOB）</summary>
        public byte[]? Embedding { get; set; }

        /// <summary>重要性 (0.0-1.0)。随时间衰减（半衰期模型），命中时提升。做梦巡逻时调整。</summary>
        public float Importance { get; set; } = 0.5f;

        /// <summary>确定性 (0.0-1.0)。不随时间衰减，仅由证据（用户确认/矛盾/新信息推翻）改变。</summary>
        public float Certainty { get; set; } = 1.0f;

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

        // ---- 命中追踪 ----

        /// <summary>被 recall 命中的次数</summary>
        public int RecallCount { get; set; } = 0;

        /// <summary>上次被 recall 命中的时间</summary>
        public DateTime? LastRecalledAt { get; set; }

        // ---- 生命周期 ----

        /// <summary>是否为永久记忆</summary>
        public bool IsPersistent { get; set; } = true;

        /// <summary>临时记忆的过期时间</summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>最后访问时间</summary>
        public DateTime LastAccessedAt { get; set; } = DateTime.Now;

        /// <summary>是否被更新的矛盾记忆取代（保留在库中，不主动 recall）</summary>
        public bool IsSuperseded { get; set; } = false;
    }
}
