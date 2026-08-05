using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 框架事件实体。记录框架运行过程中的重要事件（L0/L1 记录层级），
    /// 不进 Claims/Journals 记忆库，仅供调试、复盘与情绪信号参考。
    /// </summary>
    [Table("FrameworkEvents")]
    internal class FrameworkEvent
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>
        /// 记录层级："L0" = 纯机械运行日志（工具失败/超时/重试），"L1" = 重要框架标记（连续失败/用户否定等，可携带情绪信号）。
        /// </summary>
        public string Level { get; set; } = "L0";

        /// <summary>事件类型：ProcessStarted/ProcessStopped/工具失败/超时/用户否定等。</summary>
        public string Type { get; set; } = "";

        /// <summary>结构化详情 JSON（记录事件上下文与参数等）。</summary>
        public string? DetailJson { get; set; }

        /// <summary>情绪信号 JSON（L1 可携带，形如 [{Axis, Target, Reason}]）。</summary>
        public string? EmotionSignalsJson { get; set; }

        /// <summary>事件发生时间。</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
