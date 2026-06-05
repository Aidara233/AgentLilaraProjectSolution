using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 临时记忆实体。物理独立于主记忆库，作为"记忆缓存"优先访问。
    /// 做梦时由"临时记忆整理"片段去重/合并/插入主库。
    /// </summary>
    [Table("TempMemories")]
    internal class TempMemoryEntry
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

        /// <summary>向量嵌入（写入时即生成）</summary>
        public byte[]? Embedding { get; set; }

        /// <summary>来源消息ID</summary>
        public int? SourceMessageId { get; set; }

        /// <summary>置信度：high=用户明确陈述, low=模型推断或模糊提及</summary>
        public string Confidence { get; set; } = "high";

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>热度（0~∞）。被召回时滚雪球，衰减时下降。低于0.15触发入库。</summary>
        public float Heat { get; set; } = 0.3f;

        /// <summary>生成该 embedding 的模型名称</summary>
        public string? EmbeddingModel { get; set; }
    }
}
