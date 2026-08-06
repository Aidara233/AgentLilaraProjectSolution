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

        /// <summary>
        /// 按条件查询聊天记录。游标模式（AfterId/BeforeId）需指定单个频道；
        /// 其余条件（Keyword/时间范围）可跨频道或全频道，至少提供一个条件。
        /// 结果按时间升序返回。
        /// </summary>
        Task<List<MessageSummary>> SearchChatAsync(ChannelQuery query);

        // —— 消息输出 ——

        /// <summary>
        /// 发送消息到频道（文本 + 可选内联图片 &lt;img hash="..." /&gt; &lt;img work="..." /&gt;，支持图文混排）。
        /// hash:xxx 引用 ImageStorage 图库图片，work 引用 Storage/Workspace/ 下相对路径。
        /// 实现层自动解析 &lt;at user="name"/&gt; &lt;reply id="xxx"/&gt; 标签。
        /// </summary>
        Task<string?> SendMessageAsync(int channelId, string content);

        /// <summary>
        /// 发送独立媒体（语音/视频/贴纸，不参与混排）。
        /// identifier: hash:xxx（图库）或 Workspace 相对路径。
        /// </summary>
        Task<string?> SendMediaAsync(int channelId, string mediaType, string identifier);

        /// <summary>
        /// 发送文件附件（走独立上传 API）。
        /// filePath: hash:xxx（图库）或 Workspace 相对路径。
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

        /// <summary>占用该频道的会话 id（无占用为空）。</summary>
        public string? SessionId { get; set; }

        /// <summary>占用会话类型（express/working/task，仅占用时有值）。</summary>
        public string? SessionType { get; set; }

        /// <summary>频道最后一条消息的时间（无消息为空）。</summary>
        public System.DateTime? LastActive { get; set; }
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
        public bool IsBot { get; set; }
    }

    /// <summary>
    /// 聊天记录查询条件。至少提供一个有效条件（频道/关键词/时间范围/游标）。
    /// 游标模式（AfterId/BeforeId）仅支持单个频道；搜索模式（Keyword/时间范围）可跨频道或全频道。
    /// </summary>
    public class ChannelQuery
    {
        /// <summary>频道名列表。空/缺省 = 全部频道。</summary>
        public List<string>? ChannelNames { get; set; }

        /// <summary>内容关键词，匹配消息文本或发送者名。</summary>
        public string? Keyword { get; set; }

        /// <summary>起始时间（含）。</summary>
        public System.DateTime? TimeFrom { get; set; }

        /// <summary>结束时间（含）。</summary>
        public System.DateTime? TimeTo { get; set; }

        /// <summary>增量拉取：返回 Id &gt; AfterId 的消息（升序）。需指定单个频道。</summary>
        public int? AfterId { get; set; }

        /// <summary>翻旧：返回 Id &lt;= BeforeId 的最近 N 条（升序）。需指定单个频道。</summary>
        public int? BeforeId { get; set; }

        /// <summary>返回条数，1–100，默认 20。</summary>
        public int Limit { get; set; } = 20;
    }
}
