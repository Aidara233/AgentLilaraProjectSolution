using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// ReviewEngine 暴露给工具的接口。工具通过 IToolContext.Require&lt;IReviewAccess&gt;() 获取。
    /// </summary>
    public interface IReviewAccess
    {
        // 游标
        int? CursorMessageId { get; }
        int? CursorChannelId { get; }
        void MoveCursor(int? messageId, int? channelId);

        // 消息读取
        Task<List<ReviewMessageDto>> BrowseAsync(int count);
        Task<List<ReviewMessageDto>> SearchMessagesAsync(string? query, int? channelId, int? personId, string? timeStart, string? timeEnd, int limit);

        // 人物
        Task<ReviewPersonDto?> GetPersonAsync(int personId);

        // 信标
        Task<List<ReviewBeaconDto>> GetUnprocessedBeaconsAsync();

        // 评价缓冲
        void AddEvaluation(string targetType, int targetId, string dimension, string rating);

        // 思考笔记
        string ThinkingNotes { get; set; }

        // 进度
        void SaveProgress();
        void ClearProgress();

        // 行动日志
        Task LogActionAsync(string actionType, string summary, string? detailJson = null);

        // 访问追踪
        void TrackChannel(int channelId);
        void TrackPerson(int personId);
    }

    public class ReviewMessageDto
    {
        public int Id { get; set; }
        public string Time { get; set; } = "";
        public string SenderName { get; set; } = "";
        public int? PersonId { get; set; }
        public string Content { get; set; } = "";
        public bool IsFromBot { get; set; }
    }

    public class ReviewPersonDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Aliases { get; set; }
        public string? FastMemory { get; set; }
        public string TrustLevel { get; set; } = "";
        public int AlertLevel { get; set; }
        public List<ReviewDimensionDto> Dimensions { get; set; } = new();
    }

    public class ReviewDimensionDto
    {
        public string Dimension { get; set; } = "";
        public float Value { get; set; }
    }

    public class ReviewBeaconDto
    {
        public int Id { get; set; }
        public int? MessageId { get; set; }
        public int? ChannelId { get; set; }
        public int? PersonId { get; set; }
        public string Content { get; set; } = "";
        public string Source { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }
}
