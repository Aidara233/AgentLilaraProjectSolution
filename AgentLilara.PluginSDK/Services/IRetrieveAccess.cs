using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 检索访问接口。供 remember 工具 / 经历注入调用 RetrieveService。
    /// 检索中立：不做频道奖励，仅显式条件过滤。
    /// </summary>
    public interface IRetrieveAccess
    {
        /// <summary>关键词检索，返回混合结果（Claim/Journal）。</summary>
        Task<RetrieveResultDto> SearchAsync(string query, int limit = 20);

        /// <summary>按单个关键词精确查。</summary>
        Task<List<RetrieveHitDto>> GetByKeywordAsync(string keyword, int limit = 20);
    }

    /// <summary>检索结果。</summary>
    public class RetrieveResultDto
    {
        public List<RetrieveHitDto> Hits { get; set; } = new();
        public string Query { get; set; } = "";
        public System.DateTime Timestamp { get; set; }
    }

    /// <summary>单条命中。</summary>
    public class RetrieveHitDto
    {
        public int Id { get; set; }
        public string Kind { get; set; } = "";      // "Claim" | "Journal"
        public string? Raw { get; set; }
        public int MatchCount { get; set; }
        public float Credibility { get; set; }
        public bool IsInference { get; set; }
        public string? UtteranceId { get; set; }
        public List<string> SubEvidence { get; set; } = new();
        public System.DateTime OccurredAt { get; set; }
    }
}
