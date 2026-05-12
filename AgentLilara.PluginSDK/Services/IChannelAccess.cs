using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 频道信息和通知接口。
    /// </summary>
    public interface IChannelAccess
    {
        /// <summary>向频道循环注入通知。</summary>
        void NotifyChannel(int channelId, string content);

        /// <summary>获取所有频道列表。</summary>
        Task<List<ChannelSummary>> GetAllChannelsAsync();

        /// <summary>获取频道详情。</summary>
        Task<ChannelDetail?> GetChannelDetailAsync(int channelId);

        /// <summary>获取频道消息历史。</summary>
        Task<List<MessageSummary>> GetMessagesAsync(int channelId, int count = 20);
    }

    public class ChannelSummary
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string Platform { get; set; } = "";
        public int MessageCount { get; set; }
        public bool HasActiveEngine { get; set; }
    }

    public class ChannelDetail
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string Platform { get; set; } = "";
        public string PlatformChannelId { get; set; } = "";
        public int MessageCount { get; set; }
    }

    public class MessageSummary
    {
        public long Id { get; set; }
        public string UserName { get; set; } = "";
        public string Content { get; set; } = "";
        public System.DateTime Timestamp { get; set; }
    }
}
