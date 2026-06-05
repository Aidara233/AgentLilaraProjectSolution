using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 人设记忆（假记忆）。预设的角色经历和背景知识，仅在聊天时参与召回。
    /// 独立于主记忆库，不进入做梦流程，不会被整合或遗忘。
    /// </summary>
    [Table("PersonaMemories")]
    internal class PersonaMemoryEntry
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>记忆内容</summary>
        public string Content { get; set; } = "";

        /// <summary>向量嵌入</summary>
        public byte[]? Embedding { get; set; }

        /// <summary>分类标签（如 "经历"、"偏好"、"背景"），用于管理和筛选</summary>
        public string? Category { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>生成该 embedding 的模型名称</summary>
        public string? EmbeddingModel { get; set; }
    }
}
