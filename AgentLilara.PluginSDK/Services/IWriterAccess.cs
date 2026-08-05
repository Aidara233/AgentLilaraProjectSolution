using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 写入口访问接口。供 wait/工具调用 Writer（唯一写入口）。
    /// 插件只提交 WriteBatch，不直接碰表。
    /// </summary>
    public interface IWriterAccess
    {
        /// <summary>写入一批（原子）。返回受影响断言 Id 列表。</summary>
        Task<List<int>> WriteBatchAsync(WriteBatchDto batch);

        /// <summary>快速写一条断言。</summary>
        Task<int> WriteClaimAsync(ClaimDto claim);

        /// <summary>快速写一条经历。</summary>
        Task<int> WriteJournalAsync(JournalDto journal);
    }

    /// <summary>一批写入（容器）。</summary>
    public class WriteBatchDto
    {
        public List<ClaimDto> Claims { get; set; } = new();
        public List<JournalDto> Journals { get; set; } = new();
    }

    /// <summary>断言写入 DTO。</summary>
    public class ClaimDto
    {
        public SlotDto? Subject { get; set; }
        public SlotDto? Predicate { get; set; }
        public SlotDto? Object { get; set; }
        public string? Raw { get; set; }
        public string? SourceJson { get; set; }
        public string? UtteranceId { get; set; }
        public float? Strength { get; set; }
        public float? Credibility { get; set; }
        public bool IsInference { get; set; }
    }

    /// <summary>槽位 DTO（{ type, index, ref? }）。</summary>
    public class SlotDto
    {
        public string Type { get; set; } = "entity";
        public string Index { get; set; } = "";
        public int? Ref { get; set; }
    }

    /// <summary>经历写入 DTO。</summary>
    public class JournalDto
    {
        public string? Content { get; set; }
        public string? Kind { get; set; }
        public string? Source { get; set; }
        public int? ChannelId { get; set; }
        public int? SessionId { get; set; }
        public string? EmotionSignalsJson { get; set; }
    }
}
