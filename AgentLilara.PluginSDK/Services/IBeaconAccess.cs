using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 信标访问接口。任何人都能创建信标，消费者按 Consumer 字段过滤属于自己的信标。
    /// </summary>
    public interface IBeaconAccess
    {
        /// <summary>创建一个信标，返回信标 ID。</summary>
        Task<int> CreateAsync(string content, string source, string consumer,
            int? channelId = null, int? personId = null, int? messageId = null);

        /// <summary>获取指定消费者未处理的信标列表。</summary>
        Task<List<BeaconDto>> GetUnprocessedAsync(string consumer);

        /// <summary>标记信标为已处理。</summary>
        Task MarkProcessedAsync(int id);
    }

    public class BeaconDto
    {
        public int Id { get; set; }
        public int? MessageId { get; set; }
        public int? ChannelId { get; set; }
        public int? PersonId { get; set; }
        public string Content { get; set; } = "";
        public string Source { get; set; } = "";
        public string Consumer { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }
}
