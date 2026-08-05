using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 情绪访问接口。插件只读情绪状态 + 提交情绪信号。
    /// 不暴露：直接改绝对值、删快照、改热容、改乘数（02 情绪模型 SDK 边界）。
    /// </summary>
    public interface IEmotionAccess
    {
        /// <summary>读取当前五轴状态。</summary>
        Task<EmotionStateDto> GetCurrentAsync();

        /// <summary>提交一条情绪信号（目标值吸引子，非增量），实时应用。</summary>
        Task SubmitSignalAsync(EmotionSignal signal);

        /// <summary>最近 N 条情绪快照（时间倒序）。</summary>
        Task<List<EmotionSnapshotDto>> GetRecentSnapshotAsync(int limit = 20);
    }

    /// <summary>情绪信号。提交某根轴的【目标值】（吸引子），不是直接增量。</summary>
    public class EmotionSignal
    {
        /// <summary>轴名：excitement | joy | alertness | satisfaction | optimism</summary>
        public string Axis { get; set; } = "";

        /// <summary>目标值 [-1, 1]（吸引子，向它趋近）</summary>
        public float Target { get; set; } = 0;

        /// <summary>触发原因（审计/展示）</summary>
        public string? Reason { get; set; }
    }

    /// <summary>当前五轴状态快照。</summary>
    public class EmotionStateDto
    {
        public float Excitement { get; set; }
        public float Joy { get; set; }
        public float Alertness { get; set; }
        public float Satisfaction { get; set; }
        public float Optimism { get; set; }

        /// <summary>最近一次更新时间。</summary>
        public DateTime LastUpdatedAt { get; set; }
    }

    /// <summary>情绪快照（历史轨迹，随 Writer 事务落库）。</summary>
    public class EmotionSnapshotDto
    {
        public float Excitement { get; set; }
        public float Joy { get; set; }
        public float Alertness { get; set; }
        public float Satisfaction { get; set; }
        public float Optimism { get; set; }

        /// <summary>触发来源 Journal/L1 引用 JSON</summary>
        public string? SourceRef { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
