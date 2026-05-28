using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 记忆系统访问接口。提供完整的数据访问能力，插件自行决定检索策略。
    /// </summary>
    public interface IMemoryAccess
    {
        // ===== 主记忆库 =====

        /// <summary>写入一条主记忆。</summary>
        Task<int> StoreAsync(MemoryWriteRequest request);

        /// <summary>语义搜索（返回原始相似度分数）。</summary>
        Task<List<MemoryEntry>> SemanticSearchAsync(string query, int limit = 20,
            int? personId = null, int? channelId = null);

        /// <summary>条件筛选。</summary>
        Task<List<MemoryEntry>> FilterAsync(MemoryFilter filter);

        /// <summary>批量读取（分页）。高级用户接管记忆系统用。</summary>
        Task<List<MemoryEntry>> ListAsync(int offset = 0, int limit = 100);

        /// <summary>主记忆总数。</summary>
        Task<int> CountAsync();

        /// <summary>按 ID 获取单条。</summary>
        Task<MemoryEntry?> GetByIdAsync(int id);

        /// <summary>删除。</summary>
        Task DeleteAsync(int id);

        /// <summary>更新内容（自动重算 embedding）。</summary>
        Task UpdateAsync(int id, string newContent);

        // ===== 关联图 =====

        /// <summary>获取关联记忆（含关联元数据）。</summary>
        Task<List<LinkedMemoryEntry>> GetLinkedAsync(int memoryId);

        /// <summary>创建关联。</summary>
        Task LinkAsync(int fromId, int toId, float strength = 1.0f, string linkType = "semantic");

        /// <summary>删除关联。</summary>
        Task UnlinkAsync(int fromId, int toId);

        // ===== 向量操作 =====

        /// <summary>获取已存储的 embedding 向量。</summary>
        Task<float[]?> GetEmbeddingAsync(int memoryId);

        /// <summary>计算文本的 embedding（不存储）。</summary>
        Task<float[]> ComputeEmbeddingAsync(string text);

        /// <summary>直接用向量搜索。</summary>
        Task<List<MemoryEntry>> VectorSearchAsync(float[] embedding, int limit = 20,
            int? personId = null, int? channelId = null);

        // ===== 临时记忆库（结构更简单） =====

        /// <summary>写入临时记忆。</summary>
        Task<int> StoreTempAsync(TempMemoryWriteRequest request);

        /// <summary>语义搜索临时记忆。</summary>
        Task<List<TempMemoryEntry>> SearchTempAsync(string query, int limit = 20);

        /// <summary>批量读取临时记忆（分页）。</summary>
        Task<List<TempMemoryEntry>> ListTempAsync(int offset = 0, int limit = 100);

        /// <summary>临时记忆总数。</summary>
        Task<int> CountTempAsync();

        /// <summary>删除临时记忆。</summary>
        Task DeleteTempAsync(int id);
    }

    // ===== 数据类型 =====

    public class MemoryEntry
    {
        public int Id { get; set; }
        public string Content { get; set; } = "";
        public string? Type { get; set; }
        public string? Subject { get; set; }
        public int? PersonId { get; set; }
        public int? ChannelId { get; set; }
        public float Importance { get; set; }
        public string? Confidence { get; set; }
        public bool IsPersistent { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public float Score { get; set; }
    }

    public class TempMemoryEntry
    {
        public int Id { get; set; }
        public string Content { get; set; } = "";
        public string? Type { get; set; }
        public string? Subject { get; set; }
        public int? PersonId { get; set; }
        public int? ChannelId { get; set; }
        public DateTime CreatedAt { get; set; }
        public float Score { get; set; }
    }

    public class MemoryWriteRequest
    {
        public string Content { get; set; } = "";
        public string? Type { get; set; }
        public string? Subject { get; set; }
        public int? PersonId { get; set; }
        public int? ChannelId { get; set; }
        public float Importance { get; set; } = 0.5f;
        public string Confidence { get; set; } = "high";
        public bool IsPersistent { get; set; } = true;
        public DateTime? ExpiresAt { get; set; }
    }

    public class TempMemoryWriteRequest
    {
        public string Content { get; set; } = "";
        public string? Type { get; set; }
        public string? Subject { get; set; }
        public int? PersonId { get; set; }
        public int? ChannelId { get; set; }
    }

    public class MemoryFilter
    {
        public int? PersonId { get; set; }
        public int? ChannelId { get; set; }
        public string? Type { get; set; }
        public string? Subject { get; set; }
        public string? KeywordContains { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public DateTime? CreatedBefore { get; set; }
        public float? MinImportance { get; set; }
        public int Offset { get; set; } = 0;
        public int Limit { get; set; } = 50;
    }

    public class LinkedMemoryEntry
    {
        public int LinkId { get; set; }
        public int MemoryId { get; set; }
        public string Content { get; set; } = "";
        public string? Type { get; set; }
        public string? Subject { get; set; }
        public int? PersonId { get; set; }
        public int? ChannelId { get; set; }
        public float Importance { get; set; }
        public string? Confidence { get; set; }
        public float Strength { get; set; }
        public string LinkType { get; set; } = "";
        public DateTime LinkedAt { get; set; }
    }
}
