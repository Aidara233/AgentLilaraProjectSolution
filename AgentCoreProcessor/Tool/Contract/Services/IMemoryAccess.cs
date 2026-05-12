using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool.Contract.Services
{
    /// <summary>
    /// 记忆系统访问接口。
    /// </summary>
    public interface IMemoryAccess
    {
        /// <summary>写入一条临时记忆（框架自动关联 person/channel）。</summary>
        Task StoreAsync(string content, int? personId = null, int? channelId = null);

        /// <summary>按关键词/语义搜索主记忆库。</summary>
        Task<List<MemorySearchResult>> SearchAsync(string query, int limit = 10,
            int? channelId = null, int? personId = null);
    }

    public class MemorySearchResult
    {
        public int Id { get; set; }
        public string Content { get; set; } = "";
        public float Score { get; set; }
        public string? Subject { get; set; }
        public int? PersonId { get; set; }
        public int? ChannelId { get; set; }
    }
}
