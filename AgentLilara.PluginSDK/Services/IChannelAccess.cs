using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 频道信息和通知接口。
    /// </summary>
    public interface IChannelAccess
    {
        /// <summary>获取所有频道列表。</summary>
        Task<List<ChannelSummary>> GetAllChannelsAsync();

        /// <summary>获取频道详情。</summary>
        Task<ChannelDetail?> GetChannelDetailAsync(int channelId);

        /// <summary>获取频道消息历史。</summary>
        Task<List<MessageSummary>> GetMessagesAsync(int channelId, int count = 20);

        /// <summary>调整频道亲和度（delta 正值增加，负值降低）。</summary>
        Task UpdateAffinityAsync(int channelId, float delta);

        // —— 消息输出 ——

        /// <summary>
        /// 发送消息到频道（文本 + 可选内联图片 <img path="..."/>，支持图文混排）。
        /// 实现层自动解析 <at user="name"/> <reply id="xxx"/> 标签。
        /// </summary>
        Task<string?> SendMessageAsync(int channelId, string content);

        /// <summary>
        /// 发送独立媒体（语音/视频/贴纸，不参与混排）。
        /// </summary>
        Task<string?> SendMediaAsync(int channelId, string mediaType, string pathOrUrl);

        /// <summary>
        /// 发送文件附件（走独立上传 API）。
        /// </summary>
        Task<string?> SendFileAsync(int channelId, string filePath, string? fileName = null);
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
